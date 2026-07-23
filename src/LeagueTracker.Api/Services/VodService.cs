using System.IO.Compression;
using System.Text.Json;

namespace LeagueTracker.Api.Services;

/// Stores the full-game VODs the render agent records while the player is in
/// a live game (as opposed to FullGameService's replay re-renders): the mp4,
/// the recording sidecar (clock map, encoder, who played), the input
/// telemetry, and a thumbnail, under data/vods/{matchId}. Files-as-truth
/// like every artifact family; the db is never written.
public sealed class VodService(DataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private string VodsRoot => Path.Combine(paths.DataDir, "vods");

    private string? DirFor(string matchId) =>
        matchId.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_') ? null : Path.Combine(VodsRoot, matchId);

    public string? VideoPath(string matchId) => ExistingFile(matchId, "vod.mp4");
    public string? MetaPath(string matchId) => ExistingFile(matchId, "meta.json");
    public string? EventsPath(string matchId) => ExistingFile(matchId, "events.csv.gz");
    public string? ThumbPath(string matchId) => ExistingFile(matchId, "thumb.jpg");

    public string? TargetPath(string matchId, string file) =>
        DirFor(matchId) is { } dir ? Path.Combine(dir, file) : null;

    private string? ExistingFile(string matchId, string file)
    {
        if (DirFor(matchId) is not { } dir) return null;
        var path = Path.Combine(dir, file);
        return File.Exists(path) ? path : null;
    }

    /// What the match page needs to decide whether (and how) to show the
    /// review player: the recording sidecar plus the derived APM series.
    public object Status(string matchId)
    {
        var videoPath = VideoPath(matchId);
        object? meta = null;
        if (MetaPath(matchId) is { } metaPath)
        {
            try { meta = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(metaPath)); }
            catch { /* sidecar unreadable - the VOD still plays */ }
        }
        var youtubeUrl = ReadLink(matchId);
        var apm = ApmSeries(matchId);
        // A match can have review data in three shapes: a hosted mp4, a
        // YouTube link over sidecar data (the storage-free mode), or sidecars
        // still waiting for their link. Nothing at all = no card.
        if (videoPath is null && meta is null && youtubeUrl is null && apm is null) return new { exists = false };
        return new
        {
            exists = videoPath is not null,
            sizeMb = videoPath is null ? (int?)null : (int)(new FileInfo(videoPath).Length / 1024 / 1024),
            youtubeUrl,
            meta,
            apm,
        };
    }

    /// The player's own YouTube upload of this game - the video lives there,
    /// the tracker only keeps the small review data around it.
    public string? ReadLink(string matchId)
    {
        if (ExistingFile(matchId, "youtube.txt") is not { } path) return null;
        var url = File.ReadAllText(path).Trim();
        return url.Length > 0 ? url : null;
    }

    public void SaveLink(string matchId, string? url)
    {
        if (TargetPath(matchId, "youtube.txt") is not { } path) return;
        if (url is not { Length: > 0 })
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, url);
    }

    /// Actions-per-minute over the game in 10s buckets, derived from the
    /// input telemetry (key/click/wheel presses; cursor motion is not an
    /// "action"). Computed once and cached next to the telemetry - the csv
    /// runs to ~35k rows per minute of game.
    public object? ApmSeries(string matchId)
    {
        if (DirFor(matchId) is not { } dir) return null;
        var cache = Path.Combine(dir, "apm.json");
        if (File.Exists(cache))
        {
            try { return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(cache)); }
            catch { /* recompute below */ }
        }
        if (EventsPath(matchId) is not { } eventsPath) return null;

        try
        {
            const int BucketSec = 10;
            var buckets = new List<int>();
            using var file = File.OpenRead(eventsPath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            reader.ReadLine(); // header
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(',');
                if (parts.Length < 2 || parts[1] is not ("key_down" or "mouse_down" or "wheel")) continue;
                if (!long.TryParse(parts[0], out var tMs)) continue;
                var bucket = (int)(tMs / 1000 / BucketSec);
                while (buckets.Count <= bucket) buckets.Add(0);
                buckets[bucket]++;
            }
            if (buckets.Count == 0) return null;
            var series = new
            {
                bucketSec = BucketSec,
                // counts-per-bucket scaled to per-minute, the unit players know
                apm = buckets.Select(c => c * 60 / BucketSec).ToArray(),
                averageApm = (int)Math.Round(buckets.Sum() * 60.0 / (buckets.Count * BucketSec)),
            };
            File.WriteAllText(cache, JsonSerializer.Serialize(series, Json));
            return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(cache));
        }
        catch
        {
            return null;
        }
    }

    public bool HasVod(string matchId) => VideoPath(matchId) is not null;

    public void Delete(string matchId)
    {
        if (DirFor(matchId) is { } dir && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
