using System.Text.Json;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// Rebuilds everything timeline-derived (deaths, positions, kills, objectives,
/// item events, movement metrics, loadouts) from the raw per-game JSON on disk.
/// The db is an index over those files - when a new derivation lands, this
/// recomputes it for history without touching the Riot API, LP records or
/// captured ranks.
public sealed class AnalyticsReprocessService(
    LeagueDbContext db,
    TrackedPlayerService player,
    JobStatusService status,
    ILogger<AnalyticsReprocessService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task ReprocessAsync(CancellationToken ct)
    {
        try
        {
            var puuid = await player.GetPuuidAsync(ct);
            var matchIds = await db.Matches.Select(m => m.Id).OrderBy(id => id).ToListAsync(ct);

            var done = 0;
            var missing = 0;
            var failed = 0;
            foreach (var matchId in matchIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!await ReprocessOneAsync(matchId, puuid, ct)) missing++;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogWarning(ex, "Reprocess failed for {MatchId}", matchId);
                }
                done++;
                status.Report(done, matchIds.Count, $"{done}/{matchIds.Count} reprocessed ({missing} without raw file, {failed} failed)");
            }
            status.Finish($"done - {done} matches, {missing} without raw file, {failed} failed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reprocess failed");
            status.Finish($"failed: {ex.Message}");
            throw;
        }
    }

    private async Task<bool> ReprocessOneAsync(string matchId, string puuid, CancellationToken ct)
    {
        var match = await db.Matches.Include(m => m.Participants).FirstAsync(m => m.Id == matchId, ct);
        if (match.RawPath is not { Length: > 0 } || !File.Exists(match.RawPath)) return false;

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(match.RawPath, ct));
        var matchRaw = doc.RootElement.GetProperty("match").GetRawText();
        var timelineRaw = doc.RootElement.TryGetProperty("timeline", out var tl) && tl.ValueKind is not JsonValueKind.Null
            ? tl.GetRawText() : null;

        var dto = JsonSerializer.Deserialize<RiotMatchDto>(matchRaw, Json)!;
        // PUUIDs are encrypted per API key, so raw files written under an older
        // key never contain the current puuid. The participant row's IsMe flag
        // (set at ingest time) is the stable identity; puuid is the fallback
        // for rows imported before the flag existed.
        var mePid = match.Participants.FirstOrDefault(x => x.IsMe)?.ParticipantId;
        var me = mePid is { } pid
            ? dto.Info.Participants.First(p => p.ParticipantId == pid)
            : dto.Info.Participants.First(p => p.Puuid == puuid);

        foreach (var p in dto.Info.Participants)
        {
            if (match.Participants.FirstOrDefault(x => x.ParticipantId == p.ParticipantId) is not { } existing) continue;
            existing.Summoner1Id = p.Summoner1Id;
            existing.Summoner2Id = p.Summoner2Id;
            existing.PrimaryStyleId = p.PrimaryStyleId;
            existing.SubStyleId = p.SubStyleId;
            existing.KeystoneId = p.KeystoneId;
            existing.Items = p.ItemsCsv;
            existing.SkillshotsHit = p.Challenges?.SkillshotsHit;
            existing.SkillshotsDodged = p.Challenges?.SkillshotsDodged;
            existing.SkillshotDodgesLateWindow = p.Challenges?.DodgeSkillShotsSmallWindow;
            existing.KillParticipation = p.Challenges?.KillParticipation;
            existing.PerksJson = MatchIngestService.PerksJsonFor(p);
            existing.Spell1Casts = p.Spell1Casts;
            existing.Spell2Casts = p.Spell2Casts;
            existing.Spell3Casts = p.Spell3Casts;
            existing.Spell4Casts = p.Spell4Casts;
            existing.Summoner1Casts = p.Summoner1Casts;
            existing.Summoner2Casts = p.Summoner2Casts;
            existing.PingsJson = MatchIngestService.PingsJsonFor(p);
        }
        MatchIngestService.ApplyMatchDtoStats(match, dto.Info, me);
        match.ChallengesJson = MatchIngestService.ExtractChallengesJson(matchRaw, puuid);

        // Children cascade at the db level, so clearing the parents clears all
        // derived rows before the fresh ones land.
        await db.Deaths.Where(d => d.MatchId == matchId).ExecuteDeleteAsync(ct);
        await db.PositionSamples.Where(p => p.MatchId == matchId).ExecuteDeleteAsync(ct);
        await db.KillEvents.Where(k => k.MatchId == matchId).ExecuteDeleteAsync(ct);
        await db.ObjectiveEvents.Where(o => o.MatchId == matchId).ExecuteDeleteAsync(ct);
        await db.ItemEvents.Where(i => i.MatchId == matchId).ExecuteDeleteAsync(ct);

        if (timelineRaw is not null)
        {
            MatchIngestService.ApplyTimelineAnalysis(match, TimelineAnalyzer.Analyze(timelineRaw, dto.Info, me));
            match.HasTimeline = true;
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();   // 300+ matches of tracked position samples would balloon otherwise
        return true;
    }
}
