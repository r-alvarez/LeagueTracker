using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

// The linked games whose upload went without chapters (match not analysed in
// time, a hand-pasted link, a pre-chapters agent). It runs after the day's
// games are uploaded, on whatever quota the day left, so it never competes
// with an upload; a pass the quota cut short gets one more go right after the
// daily reset instead of waiting another day.
public sealed class YouTubeChapterSweeper(AccountRegistry accounts, AccountScopes scopes, ILogger<YouTubeChapterSweeper> log) : BackgroundService
{
    private enum SweepEnd { Done, QuotaSpent }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var uk = ChapterSweepSchedule.Zone("Europe/London");
        var pacific = ChapterSweepSchedule.Zone("America/Los_Angeles");
        if (uk == TimeZoneInfo.Utc || pacific == TimeZoneInfo.Utc)
        {
            log.LogWarning("Time zone data missing: YouTube chapter sweeps are scheduled on UTC instead of UK / Pacific time");
        }
        var retryAfterReset = false;
        while (!ct.IsCancellationRequested)
        {
            var next = ChapterSweepSchedule.NextRunUtc(DateTime.UtcNow, uk, pacific, retryAfterReset);
            log.LogInformation("Next YouTube chapter sweep at {NextUtc:u}", next);
            try { await Task.Delay(next - DateTime.UtcNow is { Ticks: > 0 } wait ? wait : TimeSpan.Zero, ct); }
            catch (OperationCanceledException) { return; }

            var afterReset = retryAfterReset;
            try
            {
                var end = await SweepAsync(ct);
                // The post-reset go is the last one: a backlog the whole fresh
                // quota could not clear waits for the night, not the uploads.
                retryAfterReset = end is SweepEnd.QuotaSpent && !afterReset;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning("Chapter sweep failed: {Message}", ex.Message);
                retryAfterReset = false;
            }
        }
    }

    private async Task<SweepEnd> SweepAsync(CancellationToken ct)
    {
        var written = 0;
        var waiting = 0;
        foreach (var account in accounts.All)
        {
            using var scope = scopes.Create(account);
            var db = scope.ServiceProvider.GetRequiredService<LeagueDbContext>();
            var chapters = scope.ServiceProvider.GetRequiredService<YouTubeChapterService>();
            var vods = scope.ServiceProvider.GetRequiredService<VodService>();
            var matches = await db.Matches.AsNoTracking()
                .OrderByDescending(m => m.GameEndUtc)
                .Select(m => new { m.Id, m.GameEndUtc })
                .ToListAsync(ct);
            foreach (var match in matches.Where(m => vods.ReadLink(m.Id) is not null && !chapters.IsStamped(m.Id)))
            {
                var outcome = await chapters.TryWriteAsync(match.Id, match.GameEndUtc, null, ct);
                if (outcome is ChapterOutcome.Written) written++;
                if (outcome is ChapterOutcome.Retry) waiting++;
                if (outcome is ChapterOutcome.QuotaExhausted)
                {
                    log.LogInformation("Chapter sweep stopped on the YouTube quota after {Written} video(s)", written);
                    return SweepEnd.QuotaSpent;
                }
                // Credentials are per account (a friend's channel): one
                // without the scope or a token must not hold up the others.
                if (outcome is ChapterOutcome.NeedsConsent or ChapterOutcome.NoCredentials) break;
            }
        }
        log.LogInformation("Chapter sweep done: {Written} written, {Waiting} still waiting", written, waiting);
        return SweepEnd.Done;
    }
}

public static class ChapterSweepSchedule
{
    // After the evening's games are uploaded; UK time because that is when
    // the players play.
    public static readonly TimeSpan NightlyAt = new(0, 30, 0);
    // YouTube's quota day starts at midnight Pacific; a little slack for it.
    public static readonly TimeSpan AfterQuotaReset = new(0, 15, 0);

    public static TimeZoneInfo Zone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    public static DateTime NextRunUtc(DateTime utcNow, TimeZoneInfo uk, TimeZoneInfo pacific, bool retryAfterReset)
    {
        var nightly = NextLocalUtc(utcNow, uk, NightlyAt);
        if (!retryAfterReset) return nightly;
        var reset = NextLocalUtc(utcNow, pacific, AfterQuotaReset);
        return reset < nightly ? reset : nightly;
    }

    private static DateTime NextLocalUtc(DateTime utcNow, TimeZoneInfo zone, TimeSpan timeOfDay)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        for (var day = local.Date; ; day = day.AddDays(1))
        {
            var candidate = DateTime.SpecifyKind(day + timeOfDay, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(candidate)) continue;
            var utc = TimeZoneInfo.ConvertTimeToUtc(candidate, zone);
            if (utc > utcNow) return utc;
        }
    }
}
