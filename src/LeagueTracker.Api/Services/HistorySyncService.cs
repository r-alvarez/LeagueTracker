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
            foreach (var matchId in ids)
            {
                ct.ThrowIfCancellationRequested();
                if (await db.Matches.AnyAsync(m => m.Id == matchId, ct))
                {
                    skipped++;
                }
                else
                {
                    await IngestAsync(matchId, puuid, includeTimeline, includeRanks, ct);
                }
                processed++;
                status.Report(processed, ids.Count, $"{processed}/{ids.Count} ({skipped} already present)");
            }

            // Snapshot LP so this sync becomes a bracket for future attribution runs.
            await lp.TakeSnapshotAsync(puuid, ct);
            await AttributeLpFromLedgerAsync(ct);

            status.Finish($"done - {processed} games checked, {processed - skipped} downloaded, {skipped} already present");
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
