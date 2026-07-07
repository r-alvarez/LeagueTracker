using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// Bulk history pull - the exporter's -RankedTarget / -MaxMatches modes.
/// Downloads match + timeline for every game not already in the db, with
/// participant ranks (current ranks - rank-at-game-time is not retroactively
/// available; only the live poller captures that).
public sealed class HistorySyncService(
    LeagueDbContext db,
    RiotApiClient riot,
    MatchIngestService ingest,
    LpService lp,
    RankLookupService rankLookup,
    TrackedPlayerService player,
    JobStatusService status,
    ILogger<HistorySyncService> logger)
{
    /// rankedTarget > 0: up to that many ranked games (Solo/Duo + Flex).
    /// Otherwise all queues, capped by maxMatches when > 0.
    public async Task SyncAsync(int rankedTarget, int maxMatches, bool includeTimeline, bool includeRanks, CancellationToken ct)
    {
        try
        {
            var puuid = await player.GetPuuidAsync(ct);
            var rankedOnly = rankedTarget > 0;
            var cap = rankedOnly ? rankedTarget : maxMatches;

            status.Report(0, 0, "listing match ids");
            var ids = new List<string>();
            for (var start = 0; cap <= 0 || ids.Count < cap; start += 100)
            {
                var batch = await riot.GetMatchIdsAsync(puuid, start, 100, rankedOnly, ct);
                if (batch is not { Count: > 0 }) break;
                ids.AddRange(cap > 0 ? batch.Take(cap - ids.Count) : batch);
                if (batch.Count < 100) break;
            }

            var processed = 0;
            var skipped = 0;
            var failed = 0;
            foreach (var matchId in ids)
            {
                ct.ThrowIfCancellationRequested();
                if (await db.Matches.AnyAsync(m => m.Id == matchId, ct))
                {
                    skipped++;
                }
                else
                {
                    try
                    {
                        await IngestAsync(matchId, puuid, includeTimeline, includeRanks, ct);
                    }
                    catch (RiotApiException ex) when (ex.IsAuthFailure)
                    {
                        throw;   // a dead key stops the run; one broken game must not
                    }
                    catch (Exception ex)
                    {
                        // Riot occasionally serves corrupt matches (queueId 0, no
                        // participants). Remember them so neither sync nor poller retries.
                        failed++;
                        logger.LogWarning(ex, "Skipping unprocessable match {MatchId}", matchId);
                        db.ChangeTracker.Clear();
                        if (await db.KnownMatches.FindAsync([matchId], ct) is null)
                        {
                            db.KnownMatches.Add(new KnownMatch { Id = matchId });
                            await db.SaveChangesAsync(ct);
                        }
                    }
                }
                processed++;
                status.Report(processed, ids.Count, $"{processed}/{ids.Count} ({skipped} already present, {failed} skipped)");
            }

            // Snapshot LP so this sync becomes a bracket for future attribution runs.
            await lp.TakeSnapshotAsync(puuid, ct);
            await AttributeLpFromLedgerAsync(ct);

            status.Finish($"done - {processed} games checked, {processed - skipped - failed} downloaded, {skipped} already present, {failed} unprocessable skipped");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "History sync failed");
            status.Finish($"failed: {ex.Message}");
            throw;
        }
    }

    private async Task IngestAsync(string matchId, string puuid, bool includeTimeline, bool includeRanks, CancellationToken ct)
    {
        var matchRaw = await riot.GetMatchRawAsync(matchId, ct);
        string? timelineRaw = null;
        if (includeTimeline)
        {
            try
            {
                timelineRaw = await riot.GetTimelineRawAsync(matchId, ct);
            }
            catch (RiotApiException ex) when (!ex.IsAuthFailure)
            {
                logger.LogWarning("Timeline unavailable for {MatchId}: {Message}", matchId, ex.Message);
            }
        }

        var match = await ingest.BuildMatchAsync(matchRaw, timelineRaw, puuid, includeRanks, ranksAtGameTime: false, ct);
        match.RawPath = await ingest.SaveRawAsync(matchId, matchRaw, timelineRaw, ct);

        db.Matches.Add(match);
        if (await db.KnownMatches.FindAsync([matchId], ct) is null)
        {
            db.KnownMatches.Add(new KnownMatch { Id = matchId });
        }
        await db.SaveChangesAsync(ct);
    }

    /// Repairs ranked matches whose participants have no rank data (a failed or
    /// skipped lookup at capture time) using current League-V4 entries. Ranks
    /// count as at-game-time only when the game is fresh enough not to have drifted.
    public async Task BackfillRanksAsync(int days, CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 60));
            var candidates = await db.Matches
                .Include(m => m.Participants)
                .Where(m => m.IsRanked && m.GameEndUtc >= cutoff)
                .OrderByDescending(m => m.GameEndUtc)
                .ToListAsync(ct);
            candidates = candidates.Where(m => m.Participants.Count(p => string.IsNullOrEmpty(p.Tier)) >= 5).ToList();

            var repaired = 0;
            foreach (var match in candidates)
            {
                ct.ThrowIfCancellationRequested();

                // PUUIDs are per-application: a match stored under an old/expired key
                // carries puuids the current key can't resolve in league-v4. Re-fetch
                // the match to refresh them before looking ranks up.
                try
                {
                    var freshDto = System.Text.Json.JsonSerializer.Deserialize<RiotMatchDto>(
                        await riot.GetMatchRawAsync(match.Id, ct),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    var freshByPid = freshDto.Info.Participants.ToDictionary(p => p.ParticipantId, p => p.Puuid);
                    foreach (var p in match.Participants)
                    {
                        if (freshByPid.TryGetValue(p.ParticipantId, out var fresh) && fresh is { Length: > 0 }) p.Puuid = fresh;
                    }
                }
                catch (RiotApiException ex) when (!ex.IsAuthFailure)
                {
                    logger.LogWarning(ex, "Could not refresh puuids for {MatchId}; using stored ones", match.Id);
                }

                foreach (var p in match.Participants)
                {
                    var entries = await rankLookup.GetEntriesAsync(p.Puuid, TimeSpan.FromHours(1), ct);
                    if (RankMath.SelectEntryForQueue(entries, match.QueueId) is not { Tier.Length: > 0 } entry) continue;
                    p.Tier = entry.Tier;
                    p.Division = entry.Rank;
                    p.Lp = entry.LeaguePoints;
                    p.SeasonWins = entry.Wins;
                    p.SeasonLosses = entry.Losses;
                    p.RankValue = RankMath.ToValue(entry.Tier, entry.Rank, entry.LeaguePoints);
                    p.RankQueue = RankMath.QueueLabel(entry.QueueType);
                }
                var allyValues = match.Participants.Where(p => p.IsAlly && p.RankValue != null).Select(p => (double)p.RankValue!).ToList();
                var enemyValues = match.Participants.Where(p => !p.IsAlly && p.RankValue != null).Select(p => (double)p.RankValue!).ToList();
                match.AvgAllyRankValue = allyValues is { Count: > 0 } ? allyValues.Average() : null;
                match.AvgEnemyRankValue = enemyValues is { Count: > 0 } ? enemyValues.Average() : null;
                match.AllyRanksKnown = allyValues.Count;
                match.EnemyRanksKnown = enemyValues.Count;
                match.RanksAtGameTime = DateTime.UtcNow - match.GameEndUtc < TimeSpan.FromHours(48);
                await db.SaveChangesAsync(ct);
                repaired++;
                status.Report(repaired, candidates.Count, $"{repaired}/{candidates.Count} matches repaired");
            }
            status.Finish($"done - {repaired} of {candidates.Count} rank-less matches repaired");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rank backfill failed");
            status.Finish($"failed: {ex.Message}");
            throw;
        }
    }

    /// Fill LpChange for any ranked game bracketed by exactly one win/loss in the
    /// snapshot ledger - lets history gradually gain real LP numbers as snapshots accrue.
    public async Task AttributeLpFromLedgerAsync(CancellationToken ct)
    {
        var candidates = await db.Matches
            .Where(m => m.IsRanked && m.LpChange == null && m.DurationSec >= 300)
            .ToListAsync(ct);
        foreach (var match in candidates)
        {
            var queueLabel = RankMath.QueueLabelForQueueId(match.QueueId);
            if (await lp.AttributeFromBracketsAsync(queueLabel, match.GameEndUtc, ct) is { } hit)
            {
                match.LpChange = hit.Change;
                match.LpBefore = $"{hit.Before.Tier} {hit.Before.Division} {hit.Before.Lp}";
                match.LpAfter = $"{hit.After.Tier} {hit.After.Division} {hit.After.Lp}";
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
