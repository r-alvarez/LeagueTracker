using System.IO.Compression;
using System.Text.RegularExpressions;
using LeagueTracker.Api.Riot;

namespace LeagueTracker.Api.Services;

/// Archives official .rofl replay files. Riot's match-v5 replays endpoint only
/// offers the last ~5 games via expiring pre-signed S3 links, so the poller
/// sweeps right after every capture; anything not grabbed in that window is
/// gone for good. Playback is patch-locked by the game client - the archive is
/// "review this patch", not a permanent library.
public sealed partial class ReplayArchiveService(
    RiotApiClient riot, IHttpClientFactory httpFactory, DataPaths paths, ILogger<ReplayArchiveService> logger)
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
