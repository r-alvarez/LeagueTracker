using System.IO.Compression;
using System.Text;
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
    public object Status(string matchId, bool includeApm = true)
    {
        var videoPath = VideoPath(matchId);
        object? meta = null;
        if (MetaPath(matchId) is { } metaPath)
        {
            try { meta = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(metaPath)); }
            catch { /* sidecar unreadable - the VOD still plays */ }
        }
        var youtubeUrl = ReadLink(matchId);
        var apm = includeApm ? ApmSeries(matchId) : null;
        // A match can have review data in three shapes: a hosted mp4, a
        // YouTube link over sidecar data (the storage-free mode), or sidecars
        // still waiting for their link. Nothing at all = no card.
        if (videoPath is null && meta is null && youtubeUrl is null && apm is null) return new { exists = false };
        var sizeBytes = videoPath is null ? (long?)null : new FileInfo(videoPath).Length;
        return new
        {
            exists = videoPath is not null,
            sizeMb = sizeBytes / 1024 / 1024,
            sizeBytes,
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
            using var file = File.OpenRead(eventsPath);
            // Files uploaded before the bounds existed get the absolute ceiling.
            if (ReadTelemetry(file, MaxRecordingSec) is not { Rejection: null, Buckets.Count: > 0 } read) return null;
            File.WriteAllText(cache, JsonSerializer.Serialize(Series(read.Buckets), Json));
            return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(cache));
        }
        catch
        {
            return null;
        }
    }

    // A 40-minute game logs ~35k rows a minute; the caps leave room for a
    // long game and a busy hand, not for a timestamp ten days in that used to
    // allocate 86k buckets (audit N16).
    public const int MaxTelemetryCompressedBytes = 32 * 1024 * 1024;
    public const int MaxTelemetryExpandedBytes = 256 * 1024 * 1024;
    public const int TelemetrySlackSec = 600;
    private const int MaxLinesPerSec = 1000;
    private const int MaxActionsPerSec = 20;
    private const int MaxRecordingSec = 3 * 3600;
    private const int MaxLineChars = 256;
    private const int BucketSec = 10;
    private static readonly string[] ActionTypes = ["key_down", "mouse_down", "wheel"];

    public sealed record TelemetryRejection(string Error);

    public async Task<TelemetryRejection?> StoreTelemetryAsync(string matchId, Stream body, double durationSec, CancellationToken ct)
    {
        if (TargetPath(matchId, "events.csv.gz") is not { } target) return new("not a match id");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        try
        {
            await using (var file = File.Create(temp))
            {
                await body.CopyToAsync(new BoundedStream(file, MaxTelemetryCompressedBytes), ct);
            }
            TelemetryRead read;
            using (var file = File.OpenRead(temp))
            {
                read = ReadTelemetry(file, durationSec + TelemetrySlackSec);
            }
            if (read.Rejection is { } rejection) return rejection;
            File.Move(temp, target, overwrite: true);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(target)!, "apm.json"), JsonSerializer.Serialize(Series(read.Buckets), Json));
            return null;
        }
        catch (TelemetryTooLargeException ex)
        {
            return new(ex.Message);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private sealed record TelemetryRead(TelemetryRejection? Rejection, List<int> Buckets);

    private static TelemetryRead ReadTelemetry(Stream compressed, double maxSec)
    {
        var maxMs = (long)(maxSec * 1000);
        var maxLines = (long)(maxSec * MaxLinesPerSec);
        var maxActions = (long)(maxSec * MaxActionsPerSec);
        List<int> buckets = [];
        var lines = 0L;
        var actions = 0L;
        try
        {
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(new BoundedStream(gzip, MaxTelemetryExpandedBytes));
            using var text = Lines(reader).GetEnumerator();
            if (!text.MoveNext() || !text.Current.StartsWith("t_ms", StringComparison.Ordinal)) return Reject("telemetry does not start with the t_ms header");
            while (text.MoveNext())
            {
                var line = text.Current;
                if (++lines > maxLines) return Reject($"telemetry has more than {maxLines} lines for a {maxSec:0}s recording");
                if (line.Length == 0) continue;
                var comma = line.IndexOf(',');
                if (comma <= 0 || !long.TryParse(line.AsSpan(0, comma), out var tMs)) return Reject($"telemetry line {lines + 1} has no timestamp");
                if (tMs < 0 || tMs > maxMs) return Reject($"telemetry line {lines + 1} is stamped {tMs} ms, outside the recording's 0-{maxMs} ms");
                var type = line.AsSpan(comma + 1);
                var typeEnd = type.IndexOf(',');
                if (typeEnd >= 0) type = type[..typeEnd];
                if (!IsAction(type)) continue;
                if (++actions > maxActions) return Reject($"telemetry has more than {maxActions} actions for a {maxSec:0}s recording");
                var bucket = (int)(tMs / 1000 / BucketSec);
                while (buckets.Count <= bucket) buckets.Add(0);
                buckets[bucket]++;
            }
        }
        catch (InvalidDataException ex)
        {
            return Reject($"telemetry is not readable gzip: {ex.Message}");
        }
        catch (TelemetryTooLargeException ex)
        {
            return Reject(ex.Message);
        }
        return new(null, buckets);

        static TelemetryRead Reject(string error) => new(new(error), []);
    }

    // Not StreamReader.ReadLine: it would buffer a newline-free gzip bomb whole.
    private static IEnumerable<string> Lines(TextReader reader)
    {
        var buffer = new char[64 * 1024];
        var current = new StringBuilder();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                current.Append(buffer, start, i - start);
                yield return Complete(current);
                current.Clear();
                start = i + 1;
            }
            current.Append(buffer, start, read - start);
            if (current.Length > MaxLineChars) throw TooLong();
        }
        if (current.Length > 0) yield return Complete(current);
    }

    // Checked on the finished line as well as the fragment: a newline right
    // after an oversized field slipped past the fragment check (review of N16).
    private static string Complete(StringBuilder line) =>
        line.Length > MaxLineChars ? throw TooLong() : line.ToString().TrimEnd('\r');

    private static TelemetryTooLargeException TooLong() => new($"telemetry line longer than {MaxLineChars} characters");

    private static bool IsAction(ReadOnlySpan<char> type)
    {
        foreach (var action in ActionTypes)
        {
            if (type.SequenceEqual(action)) return true;
        }
        return false;
    }

    private static object Series(List<int> buckets) => new
    {
        bucketSec = BucketSec,
        // counts-per-bucket scaled to per-minute, the unit players know
        apm = buckets.Select(c => c * 60 / BucketSec).ToArray(),
        averageApm = buckets is { Count: > 0 } ? (int)Math.Round(buckets.Sum() * 60.0 / (buckets.Count * BucketSec)) : 0,
    };

    private sealed class TelemetryTooLargeException(string message) : Exception(message);

    // Wraps the decompressed side too: a gzip bomb is small on the wire.
    private sealed class BoundedStream(Stream inner, long max) : Stream
    {
        private long _seen;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Account(read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Account(count);
            inner.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Account(count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Account(buffer.Length);
            await inner.WriteAsync(buffer, ct);
        }

        private void Account(int bytes)
        {
            _seen += bytes;
            if (_seen > max) throw new TelemetryTooLargeException($"telemetry exceeds {max / 1024 / 1024} MB");
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public bool HasVod(string matchId) => VideoPath(matchId) is not null;

    public void Delete(string matchId)
    {
        if (DirFor(matchId) is { } dir && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
