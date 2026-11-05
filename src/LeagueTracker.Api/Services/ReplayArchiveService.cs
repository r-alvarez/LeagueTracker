using System.IO.Compression;
using System.Text.RegularExpressions;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

/// Archives official .rofl replay files. Riot's match-v5 replays endpoint only
/// offers the last ~5 games via expiring pre-signed S3 links, so the poller
/// sweeps right after every capture; anything not grabbed in that window is
/// gone for good. Playback is patch-locked by the game client - the archive is
/// "review this patch", not a permanent library.
public sealed partial class ReplayArchiveService(
    RiotApiClient riot, IHttpClientFactory httpFactory, DataPaths paths, ILogger<ReplayArchiveService> logger,
    LeagueDbContext? db = null, PatchReference? patches = null, IOptions<MediaRetentionOptions>? retention = null)
{
    [GeneratedRegex(@"filename%3D%22([A-Za-z0-9]+_\d+)\.rofl", RegexOptions.IgnoreCase)]
    private static partial Regex MatchIdInUrl();

    private string ReplaysDir => Path.Combine(paths.DataDir, "replays");

    public string? PathFor(string matchId)
    {
        // Match ids come from the route; refuse anything that could escape the dir.
        if (matchId.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_')) return null;
        var path = Path.Combine(ReplaysDir, $"{matchId}.rofl");
        return File.Exists(path) ? path : null;
    }

    public HashSet<string> ArchivedMatchIds()
    {
        if (!Directory.Exists(ReplaysDir)) return [];
        return [.. Directory.EnumerateFiles(ReplaysDir, "*.rofl").Select(Path.GetFileNameWithoutExtension).OfType<string>()];
    }

    public long Delete(string matchId)
    {
        if (PathFor(matchId) is not { } path) return 0;
        var bytes = new FileInfo(path).Length;
        File.Delete(path);
        return bytes;
    }

    // Since 2026-10-24 Riot answers the replays endpoint with 403 while every
    // other match-v5 call works, so render agents fetch the .rofl through their
    // own League client (it serves any player's game on the live patch) and
    // upload it here. The sweep above stays: it costs one call per capture and
    // picks up again by itself if Riot restores access.
    private static bool IsSafeId(string matchId) =>
        matchId.Length > 0 && matchId.All(ch => char.IsLetterOrDigit(ch) || ch is '_');

    private string UnavailablePath(string matchId) => Path.Combine(ReplaysDir, $"{matchId}.unavailable");

    public bool IsUnavailable(string matchId) => IsSafeId(matchId) && File.Exists(UnavailablePath(matchId));

    /// The client said it can never serve this game (old patch, expired):
    /// asking again only wakes a render box for nothing.
    public async Task<bool> MarkUnavailableAsync(string matchId, string reason, CancellationToken ct)
    {
        if (!IsSafeId(matchId)) return false;
        Directory.CreateDirectory(ReplaysDir);
        await File.WriteAllTextAsync(UnavailablePath(matchId), $"{DateTime.UtcNow:O} {reason}", ct);
        return true;
    }

    public string? UploadStagingPath(string matchId) =>
        IsSafeId(matchId) ? Path.Combine(ReplaysDir, $"{matchId}.rofl.upload") : null;

    /// Promotes an uploaded file to the archive. Only a real replay (they all
    /// open with the "RIOT" magic) gets in: the game client would otherwise be
    /// handed junk at render time and fail far from the cause.
    public async Task<bool> AcceptUploadAsync(string matchId, string stagedPath, CancellationToken ct)
    {
        var magic = new byte[4];
        await using (var probe = File.OpenRead(stagedPath))
        {
            await probe.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, ct);
        }
        if (magic is not [(byte)'R', (byte)'I', (byte)'O', (byte)'T'])
        {
            File.Delete(stagedPath);
            return false;
        }
        var target = Path.Combine(ReplaysDir, $"{matchId}.rofl");
        File.Move(stagedPath, target, overwrite: true);
        File.Delete(UnavailablePath(matchId));
        logger.LogInformation("Archived replay {MatchId} from a render agent's client ({SizeMb:F1} MB)",
            matchId, new FileInfo(target).Length / 1024.0 / 1024.0);
        return true;
    }

    // Riot's offer covers the last ~5 games, which straddles a patch day: the
    // game before the update would download as a replay nothing can play.
    private async Task<bool> StillPlayableAsync(string matchId, CancellationToken ct)
    {
        if (db is null || patches is null || retention is null) return true;
        if (await patches.PatchesNewestFirstAsync(ct) is not { } known) return true;
        var version = await db.Matches.AsNoTracking().Where(m => m.Id == matchId).Select(m => m.GameVersion).FirstOrDefaultAsync(ct);
        return version is null || PatchReference.PatchAge(known, version) < retention.Value.ReplayPatches;
    }

    /// Downloads every offered replay we don't have yet; returns how many landed.
    public async Task<int> SweepAsync(string puuid, CancellationToken ct)
    {
        List<string> urls;
        try
        {
            urls = await riot.GetReplayUrlsAsync(puuid, ct);
        }
        catch (RiotApiException ex)
        {
            logger.LogWarning("Replay list fetch failed ({Status}); will retry on the next capture", ex.StatusCode);
            return 0;
        }

        Directory.CreateDirectory(ReplaysDir);
        var downloaded = 0;
        foreach (var url in urls)
        {
            if (MatchIdInUrl().Match(url) is not { Success: true } m) continue;
            var matchId = m.Groups[1].Value.ToUpperInvariant();
            var target = Path.Combine(ReplaysDir, $"{matchId}.rofl");
            if (File.Exists(target)) continue;
            if (!await StillPlayableAsync(matchId, ct))
            {
                logger.LogInformation("Skipping replay {MatchId}: its patch is no longer playable", matchId);
                continue;
            }

            try
            {
                // Pre-signed S3 link: plain client, no Riot auth header, no rate limiter.
                using var http = httpFactory.CreateClient("replay-download");
                await using var stream = await http.GetStreamAsync(url, ct);
                var temp = target + ".tmp";
                await using (var file = File.Create(temp))
                {
                    await stream.CopyToAsync(file, ct);
                }
                await FinalizeAsync(temp, target, ct);
                downloaded++;
                logger.LogInformation("Archived replay {MatchId} ({SizeMb:F1} MB)", matchId, new FileInfo(target).Length / 1024.0 / 1024.0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Replay download failed for {MatchId}", matchId);
            }
        }
        return downloaded;
    }

    /// Riot's S3 objects arrive gzip-compressed (Content-Encoding the plain client
    /// doesn't undo); the game client needs the raw RIOT payload, so gunzip on the
    /// magic bytes rather than trusting headers.
    private static async Task FinalizeAsync(string temp, string target, CancellationToken ct)
    {
        var header = new byte[2];
        await using (var probe = File.OpenRead(temp))
        {
            _ = await probe.ReadAsync(header.AsMemory(0, 2), ct);
        }

        if (header is [0x1F, 0x8B])
        {
            await using (var source = File.OpenRead(temp))
            await using (var gzip = new GZipStream(source, CompressionMode.Decompress))
            await using (var output = File.Create(target))
            {
                await gzip.CopyToAsync(output, ct);
            }
            File.Delete(temp);
        }
        else
        {
            File.Move(temp, target, overwrite: true);
        }
    }
}
