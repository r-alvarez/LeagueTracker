using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeagueTracker.RenderAgent.Review;

public sealed record LibrarySettings(int KeepGames = 20, double MaxGb = 20, double MinFreeGb = 10, bool KeepAll = false);
public sealed record LocalRecording(string Id, string Name, string? MatchId, string? Player, DateTime RecordedUtc,
    double DurationSec, long SizeBytes, bool Available, bool Pinned, bool Published);

/// Sidecars remain the catalog. A per-folder mutex serializes pin/delete/retention
/// across recorder and viewer processes; open readers deny deletion on Windows.
public sealed class RecordingLibrary
{
    public string Root { get; }
    public string Metadata => Path.Combine(Root, "metadata");
    private readonly string _mutex;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();

    public RecordingLibrary(string root)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _mutex = @"Local\LeagueTracker.Library." + IdFor(Root.ToUpperInvariant());
    }

    public static string RootFor(AgentConfig config) => config.RecordingsDir is { Length: > 0 } dir
        ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LeagueTracker");
    public static string IdFor(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..32];

    public T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutex);
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("The recording library is busy. Try again shortly.");
            return action();
        }
        finally { if (entered) mutex.ReleaseMutex(); }
    }

    public LibrarySettings Settings(AgentConfig config)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<LibrarySettings>(File.ReadAllText(SafeMetadata("library-settings.json")), Json);
            if (settings is not null) return Validate(settings);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
        return new LibrarySettings(20, config.MaxRecordingsGb > 0 ? config.MaxRecordingsGb : 20,
            Math.Max(1, config.MinFreeGb), config.KeepRecordingsAfterPublish);
    }

    public static LibrarySettings Validate(LibrarySettings settings)
    {
        if (settings.KeepGames is < 1 or > 500 || !double.IsFinite(settings.MaxGb) || settings.MaxGb is < 1 or > 2000
            || !double.IsFinite(settings.MinFreeGb) || settings.MinFreeGb is < 1 or > 500)
            throw new ArgumentException("Choose 1–500 games, 1–2000 GB of recordings and 1–500 GB of free space.");
        return settings;
    }

    public void SaveSettings(LibrarySettings settings) => Locked(() =>
    {
        Directory.CreateDirectory(Metadata);
        AtomicWrite(SafeMetadata("library-settings.json"), JsonSerializer.Serialize(Validate(settings), Json));
        return true;
    });

    public IReadOnlyList<LocalRecording> List(int limit = 500)
    {
        if (!Directory.Exists(Metadata) || IsReparse(Metadata)) return [];
        var result = new List<LocalRecording>();
        foreach (var file in Directory.EnumerateFiles(Metadata, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(".inflight") || name.EndsWith(".review") || name.EndsWith(".apm")) continue;
            try
            {
                result.Add(ReadRecording(name));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or ArgumentException or UnauthorizedAccessException) { }
        }
        Volatile.Write(ref _names, result.ToDictionary(r => r.Id, r => r.Name));
        return result.OrderByDescending(r => r.RecordedUtc).Take(Math.Max(limit, 1)).ToArray();
    }

    public LocalRecording Find(string id)
    {
        if (!Volatile.Read(ref _names).TryGetValue(id, out var name))
        {
            List();
            if (!Volatile.Read(ref _names).TryGetValue(id, out name)) throw new FileNotFoundException("This recording is no longer in the library.");
        }
        return ReadRecording(name);
    }

    private LocalRecording ReadRecording(string name)
    {
        var sidecar = SafeMetadata(name + ".json");
        if (new FileInfo(sidecar).Length > 2 * 1024 * 1024) throw new IOException("Invalid recording metadata.");
        var meta = JsonNode.Parse(File.ReadAllText(sidecar)) as JsonObject ?? throw new IOException("Invalid recording metadata.");
        if (meta["videoFile"]?.GetValue<string>() != name + ".mp4") throw new IOException("Invalid recording metadata.");
        var video = SafePath(name + ".mp4");
        var available = File.Exists(video) && !File.Exists(SafeMetadata(name + ".inflight.json"));
        var start = meta["recordingStartUtc"]?.GetValue<DateTime>() ?? File.GetLastWriteTimeUtc(sidecar);
        var end = meta["recordingEndUtc"]?.GetValue<DateTime>() ?? start;
        return new(IdFor(name), name, meta["matchId"]?.GetValue<string>(), meta["activePlayer"]?.GetValue<string>(), start,
            Math.Max(0, (end - start).TotalSeconds), available ? new FileInfo(video).Length : 0, available,
            File.Exists(SafeMetadata(name + ".keep")), Published(name));
    }

    public JsonNode MetadataFor(LocalRecording recording) => JsonNode.Parse(File.ReadAllText(SafeMetadata(recording.Name + ".json")))!;
    public string VideoPath(LocalRecording recording) => SafePath(recording.Name + ".mp4");
    public string? ThumbnailPath(LocalRecording recording)
    {
        var path = SafeMetadata(recording.Name + ".jpg");
        return File.Exists(path) ? path : null;
    }

    public FileStream OpenVideo(string id) => Locked(() =>
    {
        var recording = Find(id);
        if (!recording.Available) throw new FileNotFoundException("The local recording is unavailable.");
        return new FileStream(VideoPath(recording), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
    });

    public void Pin(string id, bool pinned) => Locked(() =>
    {
        var recording = Find(id);
        var path = SafeMetadata(recording.Name + ".keep");
        if (pinned) AtomicWrite(path, DateTime.UtcNow.ToString("O"));
        else File.Delete(path);
        return true;
    });

    public void Delete(string id, bool confirmUnpublished) => Locked(() =>
    {
        var recording = Find(id);
        if (recording.Pinned) throw new InvalidOperationException("Unpin this recording before deleting it.");
        if (!recording.Published && !confirmUnpublished) throw new InvalidOperationException("This may be the only copy. Confirm deletion first.");
        DeleteCore(recording);
        return true;
    });

    private void DeleteCore(LocalRecording recording)
    {
        if (File.Exists(SafeMetadata(recording.Name + ".inflight.json"))) throw new IOException("This game is still being finalized.");
        File.Delete(VideoPath(recording)); // Open playback/upload handles deny deletion.
        AtomicWrite(SafeMetadata(recording.Name + ".pruned"), DateTime.UtcNow.ToString("O"));
    }

    // .uploaded can mean only sidecars were sent. This marker is written only
    // after a full MP4 upload or confirmed YouTube processing.
    public bool Published(string name) => File.Exists(SafeMetadata(name + ".review-published"));

    public int Prune(LibrarySettings settings, bool deliveryExpected, Func<double>? freeGb = null) => Locked(() =>
    {
        if (settings.KeepAll) return 0;
        var files = List(int.MaxValue).Where(r => r.Available).ToList();
        var total = files.Sum(r => r.SizeBytes);
        var count = files.Count;
        var removed = 0;
        freeGb ??= () => FreeGb(Root);
        foreach (var file in files.OrderByDescending(r => r.Published).ThenBy(r => r.RecordedUtc))
        {
            if (count <= settings.KeepGames && total <= settings.MaxGb * 1024 * 1024 * 1024 && freeGb() >= settings.MinFreeGb) break;
            if (file.Pinned || (!file.Published && deliveryExpected) || File.GetLastWriteTimeUtc(VideoPath(file)) > DateTime.UtcNow.AddMinutes(-10)) continue;
            try { DeleteCore(file); total -= file.SizeBytes; count--; removed++; }
            catch (IOException) { /* A decoder, finalizer or uploader still owns this file. */ }
        }
        return removed;
    });

    public static double FreeGb(string root)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace / 1024d / 1024 / 1024; }
        catch { return 0; } // Unknown/unavailable drives are not permission to record.
    }

    public JsonNode? Apm(LocalRecording recording)
    {
        var cache = SafeMetadata(recording.Name + ".apm.json");
        try
        {
            if (File.Exists(cache)) return JsonNode.Parse(File.ReadAllText(cache));
            var events = SafeMetadata(recording.Name + ".events.csv.gz");
            if (!File.Exists(events)) return null;
            using var file = File.OpenRead(events);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            var buckets = new int[6 * 60 * 6]; // At most six hours; never allocate based on unchecked telemetry.
            var last = 0;
            long characters = 0;
            while (reader.ReadLine() is { } line)
            {
                characters += line.Length;
                if (characters > 512L * 1024 * 1024) return null;
                var parts = line.Split(',', 3);
                if (parts.Length < 2 || parts[1] is not ("key_down" or "mouse_down" or "wheel")
                    || !long.TryParse(parts[0], out var ms) || ms < 0 || ms / 10000 >= buckets.Length) continue;
                var i = (int)(ms / 10000);
                buckets[i]++;
                last = Math.Max(last, i);
            }
            var node = JsonSerializer.SerializeToNode(new { bucketSec = 10, apm = buckets.Take(last + 1).Select(n => n * 6),
                averageApm = (int)Math.Round(buckets.Sum() * 6d / (last + 1)) }, Json);
            AtomicWrite(cache, node!.ToJsonString());
            return node;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void AtomicWrite(string path, string contents)
    {
        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try { File.WriteAllText(temp, contents); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string SafeMetadata(string name) => SafePath(Path.Combine("metadata", name));
    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    public string SafePath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid recording path.");
        for (var path = full; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && IsReparse(path)) throw new IOException("Linked recording paths are not supported.");
            if (path.Equals(Root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }
}
