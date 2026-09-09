using System.Text.Json;

namespace LeagueTracker.RenderAgent.Review;

public sealed record CachedReply(int Status, string Body, DateTime SavedUtc, bool Cached = false, bool Offline = false);

/// Disposable cache: bounded separately from video storage, names derived from
/// identity and schema version, never from server-supplied filesystem paths.
public sealed class ReviewCache(string root)
{
    public string Root { get; } = root;
    private readonly object _gate = new();
    public CachedReply? Read(string key)
    {
        lock (_gate)
        {
            try
            {
                var file = Path.Combine(Root, RecordingLibrary.IdFor("v1:" + key) + ".json");
                if (!File.Exists(file) || new FileInfo(file).Length > 12 * 1024 * 1024) return null;
                return JsonSerializer.Deserialize<CachedReply>(File.ReadAllText(file)) is { } reply ? reply with { Cached = true } : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }
    }

    public void Write(string key, CachedReply reply)
    {
        if (reply.Status != 200 || reply.Body.Length > 8 * 1024 * 1024) return;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Root);
                RecordingLibrary.AtomicWrite(Path.Combine(Root, RecordingLibrary.IdFor("v1:" + key) + ".json"), JsonSerializer.Serialize(reply));
                Trim(Root, 128L * 1024 * 1024);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Remove(string key)
    {
        lock (_gate) File.Delete(Path.Combine(Root, RecordingLibrary.IdFor("v1:" + key) + ".json"));
    }

    public static void Trim(string root, long maxBytes)
    {
        var files = new DirectoryInfo(root).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= maxBytes) break;
            try { var size = file.Length; file.Delete(); total -= size; } catch (IOException) { }
        }
    }
}
