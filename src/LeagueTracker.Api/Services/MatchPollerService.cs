using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

/// The always-on replacement for the PowerShell watcher: polls the match list,
/// ingests every new game (match + timeline + at-game-time ranks) and attributes
/// the exact LP change once Riot's win/loss counter confirms a single new game.
public sealed class MatchPollerService(
    IServiceScopeFactory scopes,
    IOptions<RiotOptions> options,
    ILogger<MatchPollerService> logger) : BackgroundService
{
    private readonly RiotOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the web host finish starting before the first poll.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (RiotApiKeyMissingException)
            {
                logger.LogWarning("No Riot API key configured yet - poller idle. Set Riot:ApiKey, RIOT_API_KEY, or Riot:ApiKeyFile.");
            }
            catch (RiotApiException ex) when (ex.IsAuthFailure)
            {
                logger.LogError("Riot API key rejected ({Status}). Refresh the key (personal keys: https://developer.riotgames.com).", ex.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Poll pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, _options.PollSeconds)), ct);
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LeagueDbContext>();
        var riot = scope.ServiceProvider.GetRequiredService<RiotApiClient>();
        var ingest = scope.ServiceProvider.GetRequiredService<MatchIngestService>();
        var lp = scope.ServiceProvider.GetRequiredService<LpService>();
        var player = scope.ServiceProvider.GetRequiredService<TrackedPlayerService>();

        var puuid = await player.GetPuuidAsync(ct);

        // First pass ever: current history is old news - baseline it and only watch forward.
        // (Backfill is explicit via /api/sync/history, mirroring the exporter.)
        if (!await db.KnownMatches.AnyAsync(ct))
        {
            var history = await riot.GetMatchIdsAsync(puuid, 0, 20, rankedOnly: false, ct);
            db.KnownMatches.AddRange(history.Select(id => new KnownMatch { Id = id }));
            await db.SaveChangesAsync(ct);
            await lp.TakeSnapshotAsync(puuid, ct);
            logger.LogInformation("First run: baselined {Count} existing games. New games are captured from now on.", history.Count);
            return;
        }

        // Look back 15, not 5: if the key was expired for a stretch (dev keys die
        // every 24h), several games can pile up unseen before it's refreshed.
        var recent = await riot.GetMatchIdsAsync(puuid, 0, 15, rankedOnly: false, ct);
        recent.Reverse();   // oldest first, so multi-game bursts stay in order

        foreach (var matchId in recent)
        {
            if (await db.KnownMatches.FindAsync([matchId], ct) is not null) continue;
            try
            {
                await IngestNewMatchAsync(matchId, puuid, db, riot, ingest, lp, ct);
            }
            catch (RiotApiException ex) when (ex.IsAuthFailure)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                // Corrupt match payload (no participants / tracked player missing) -
                // permanent, so remember it rather than retrying every pass.
                logger.LogWarning(ex, "Skipping unprocessable match {MatchId}", matchId);
                db.ChangeTracker.Clear();
                db.KnownMatches.Add(new KnownMatch { Id = matchId });
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Transient (network, Riot 5xx) - leave unknown so the next pass retries.
                logger.LogWarning(ex, "Failed to ingest {MatchId}; will retry next pass", matchId);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task IngestNewMatchAsync(
        string matchId, string puuid, LeagueDbContext db, RiotApiClient riot,
        MatchIngestService ingest, LpService lp, CancellationToken ct)
    {
        var matchRaw = await riot.GetMatchRawAsync(matchId, ct);
        string? timelineRaw = null;
        try
        {
            timelineRaw = await riot.GetTimelineRawAsync(matchId, ct);
        }
        catch (RiotApiException ex) when (!ex.IsAuthFailure)
        {
            logger.LogWarning("Timeline unavailable for {MatchId}: {Message}", matchId, ex.Message);
        }

        var match = await ingest.BuildMatchAsync(matchRaw, timelineRaw, puuid, withRanks: true, ranksAtGameTime: true, ct);
        match.RawPath = await ingest.SaveRawAsync(matchId, matchRaw, timelineRaw, ct);

        if (match.IsRanked && match.DurationSec >= 300)
        {
            await AttributeLpAsync(match, puuid, riot, lp, ct);
        }

        db.Matches.Add(match);
        db.KnownMatches.Add(new KnownMatch { Id = matchId });
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Captured {Result} {Queue} {Champion} {K}/{D}/{A}{Lp}",
            match.Win ? "WIN" : "LOSS", match.QueueName, match.Champion, match.Kills, match.Deaths, match.Assists,
            match.LpChange is { } c ? $"  LP {(c >= 0 ? "+" : "")}{c}" : "");
    }

    private async Task AttributeLpAsync(Data.Match match, string puuid, RiotApiClient riot, LpService lp, CancellationToken ct)
    {
        var queueLabel = RankMath.QueueLabelForQueueId(match.QueueId);
        var queueType = RankMath.QueueTypeForQueueId(match.QueueId);
        var prev = await lp.GetLatestAsync(queueLabel, ct);

        // If the last snapshot already postdates this game (several games since the
        // last pass), the delta is unattributable anyway - don't wait on Riot.
        var alreadyCovered = prev is not null && match.GameEndUtc < prev.TimestampUtc;

        List<LeagueEntryDto> entries = [];
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            entries = await riot.GetLeagueEntriesAsync(puuid, ct);
            if (alreadyCovered) break;
            var mine = entries.FirstOrDefault(e => e.QueueType == queueType);
            // Riot's win/loss counter registering the game is the signal that LP has settled.
            if (mine is not null && (prev is null || mine.Wins + mine.Losses > prev.Wins + prev.Losses)) break;
            if (attempt < 5) await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }

        var rows = await lp.WriteSnapshotRowsAsync(entries, ct);
        var now = rows.FirstOrDefault(r => r.Queue == queueLabel);
        if (now is null) return;

        // Trust the delta only when exactly one game moved the counter; otherwise a
        // missed pass or dodge-LP would get blamed on this match.
        if (prev is not null && !alreadyCovered && (now.Wins + now.Losses) - (prev.Wins + prev.Losses) == 1)
        {
            match.LpChange = now.RankValue - prev.RankValue;
        }
        match.LpBefore = prev is null ? null : $"{prev.Tier} {prev.Division} {prev.Lp}";
        match.LpAfter = $"{now.Tier} {now.Division} {now.Lp}";
    }
}
