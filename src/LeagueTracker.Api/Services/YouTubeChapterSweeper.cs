using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

// Every linked game without a stamp, oldest backlog included, the way the
// clip queue works: the link endpoint pokes a pass, the timer covers the
// games whose analysis landed after their link.
public sealed class YouTubeChapterSweeper(AccountRegistry accounts, AccountScopes scopes, ILogger<YouTubeChapterSweeper> log) : BackgroundService
{
    private readonly SemaphoreSlim _poke = new(0);
    // A token without the scope fails every video the same way; hourly is
    // enough to notice the replacement without a restart.
    private DateTime _consentRefusedUtc = DateTime.MinValue;

    public void Poke() { if (_poke.CurrentCount == 0) _poke.Release(); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SweepAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning("Chapter sweep failed: {Message}", ex.Message); }
            try { await _poke.WaitAsync(TimeSpan.FromMinutes(10), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _consentRefusedUtc < TimeSpan.FromHours(1)) return;
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
                if (outcome is ChapterOutcome.NeedsConsent) { _consentRefusedUtc = DateTime.UtcNow; return; }
                if (outcome is ChapterOutcome.NoCredentials) break;
            }
        }
    }
}
