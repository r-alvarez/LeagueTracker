using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

// `Retention` section. Clips outlive replays by a patch: a patch-day review
// of last week's games is the product, while the .rofl is unplayable the
// moment the client updates.
public sealed class MediaRetentionOptions
{
    public int ClipPatches { get; set; } = 2;
    public int ReplayPatches { get; set; } = 1;
    public bool ReclaimPersonalClips { get; set; } = true;
    public double PressureHeadroomGb { get; set; } = 10;
    public double AllowanceHeadroomFraction { get; set; } = 0.1;
    public int SweepMinutes { get; set; } = 60;
}

public sealed record RetainedMatch(string Id, string GameVersion, DateTime GameEndUtc);

public sealed record RetentionReport(
    int ReplaysExpired, int ClipMatchesExpired, int PersonalClipsReclaimed, int FullGamesExpired,
    int PressureEvictions, long BytesFreed, bool Starved)
{
    public bool Changed => BytesFreed > 0 || FullGamesExpired > 0 || Starved;
}

public sealed class MediaRetentionService(
    LeagueDbContext db, ClipService clips, ReplayArchiveService replays, FullGameService full, VodService vods,
    RenderLeaseService leases, UploadQuota quota, PatchReference patchReference,
    IOptions<MediaRetentionOptions> options, IOptions<RiotOptions> riot, ILogger<MediaRetentionService> log)
{
    private const long Gb = 1024L * 1024 * 1024;

    public async Task<RetentionReport> SweepAsync(CancellationToken ct)
    {
        var patches = await patchReference.PatchesNewestFirstAsync(ct);
        var ids = replays.ArchivedMatchIds().Concat(clips.MatchesWithClips()).Distinct().ToList();
        var matches = await db.Matches.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new RetainedMatch(m.Id, m.GameVersion, m.GameEndUtc))
            .ToListAsync(ct);
        return await SweepAsync(patches, matches, ct);
    }

    public async Task<RetentionReport> SweepAsync(IReadOnlyList<string>? patches, IReadOnlyList<RetainedMatch> matches, CancellationToken ct)
    {
        var known = matches.ToDictionary(m => m.Id);
        var settings = options.Value;
        long freed = 0;
        var replaysExpired = 0;
        var clipMatchesExpired = 0;
        var reclaimedClips = 0;

        if (patches is not null)
        {
            foreach (var matchId in replays.ArchivedMatchIds())
            {
                if (!known.TryGetValue(matchId, out var match) || Busy(matchId)) continue;
                if (PatchReference.PatchAge(patches, match.GameVersion) < settings.ReplayPatches) continue;
                freed += replays.Delete(matchId);
                replaysExpired++;
            }

            foreach (var matchId in clips.MatchesWithClips().ToList())
            {
                if (!known.TryGetValue(matchId, out var match) || clips.IsKept(matchId) || Busy(matchId)) continue;
                if (PatchReference.PatchAge(patches, match.GameVersion) < settings.ClipPatches) continue;
                var expiry = clips.ExpireClips(matchId, "patch", PatchReference.PatchOf(match.GameVersion));
                freed += expiry.Bytes;
                clipMatchesExpired++;
            }
        }

        if (settings.ReclaimPersonalClips)
        {
            foreach (var matchId in clips.MatchesWithClips().ToList())
            {
                if (clips.IsKept(matchId) || Busy(matchId)) continue;
                if (!vods.HasVod(matchId) && vods.ReadLink(matchId) is null) continue;
                var reclaim = await clips.ReclaimPersonalClipsAsync(matchId, ct);
                freed += reclaim.Bytes;
                reclaimedClips += reclaim.Clips;
            }
        }

        var fullGamesExpired = full.SweepRetention(riot.Value.FullGameRetentionDays);

        var (evictions, evictedBytes, starved) = RelievePressure(known, settings);
        freed += evictedBytes;

        return new RetentionReport(replaysExpired, clipMatchesExpired, reclaimedClips, fullGamesExpired, evictions, freed, starved);
    }

    // Disk and allowance refuse uploads independently, so both are relieved.
    // Full renders go first: heaviest and rarely watched.
    private (int Evictions, long Bytes, bool Starved) RelievePressure(Dictionary<string, RetainedMatch> known, MediaRetentionOptions settings)
    {
        var headroom = quota.Headroom();
        var diskLeft = headroom.DiskLeft;
        var allowanceLeft = headroom.AllowanceLeft;
        var diskTarget = (long)(settings.PressureHeadroomGb * Gb);
        var allowanceTarget = (long)(headroom.AllowanceBytes * settings.AllowanceHeadroomFraction);
        bool UnderPressure() => diskLeft < diskTarget || allowanceLeft < allowanceTarget;
        if (!UnderPressure()) return (0, 0, false);

        var evictions = 0;
        long freed = 0;
        void Freed(long bytes)
        {
            freed += bytes;
            diskLeft += bytes;
            allowanceLeft += bytes;
            evictions++;
        }

        foreach (var render in full.UnkeptRenders().OrderBy(r => r.RenderedUtc).ToList())
        {
            if (!UnderPressure()) break;
            if (Busy(render.MatchId)) continue;
            full.Delete(render.MatchId);
            Freed(render.Bytes);
            log.LogInformation("Pressure: evicted full-game render {MatchId} ({Mb:0} MB)", render.MatchId, render.Bytes / 1024.0 / 1024);
        }

        var clipMatches = clips.MatchesWithClips()
            .Where(id => !clips.IsKept(id))
            .OrderBy(id => known.TryGetValue(id, out var m) ? m.GameEndUtc : DateTime.MinValue)
            .ToList();
        foreach (var matchId in clipMatches)
        {
            if (!UnderPressure()) break;
            if (Busy(matchId)) continue;
            var expiry = clips.ExpireClips(matchId, "pressure", known.TryGetValue(matchId, out var m) ? PatchReference.PatchOf(m.GameVersion) : null);
            Freed(expiry.Bytes);
            log.LogInformation("Pressure: evicted {Clips} clip(s) of {MatchId} ({Mb:0} MB)", expiry.Clips, matchId, expiry.Bytes / 1024.0 / 1024);
        }

        var starved = UnderPressure();
        if (starved)
        {
            log.LogWarning("Pressure: nothing left to evict and uploads will be refused; {Headroom}", quota.Headroom().Describe());
        }
        return (evictions, freed, starved);
    }

    private bool Busy(string matchId) => leases.IsLeased($"clips:{matchId}") || leases.IsLeased($"full:{matchId}");
}
