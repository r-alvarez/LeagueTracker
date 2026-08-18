using System.Diagnostics;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

// Uploads keep flowing while the player is in a game - the customer's
// expectation is that game 1 is on YouTube by the time game 2 ends - but a
// saturated uplink is felt as lag, so in-game they are paced: a share of
// the line's measured idle throughput (measured from our own unthrottled
// uploads, remembered across restarts), never below a floor. Out of game,
// full speed. The pacing happens inside each HTTP body in small slices, so
// a game starting mid-chunk is honoured within a slice, not a chunk.
public sealed class UploadThrottle
{
    // The one throttle every uploader in the process paces through; the
    // recorder installs it (it knows the game state and the config).
    public static UploadThrottle? Shared { get; set; }

    private const int SliceBytes = 64 * 1024;
    private const double InGameShare = 0.5;
    private const long FloorBytesPerSecond = 3_000_000 / 8;      // 3 Mbps
    private const long UnmeasuredBytesPerSecond = 6_000_000 / 8; // 6 Mbps until we know the line
    private const int SmoothingSamples = 8;

    private readonly AgentConfig _config;
    private readonly Func<bool> _inGame;
    private readonly string _memoryPath;
    private readonly object _gate = new();
    private double? _idleBytesPerSecond;
    private DateTime _lastSavedUtc;

    public UploadThrottle(AgentConfig config, Func<bool> inGame, string memoryPath)
    {
        _config = config;
        _inGame = inGame;
        _memoryPath = memoryPath;
        try
        {
            if (File.Exists(memoryPath) && JsonDocument.Parse(File.ReadAllText(memoryPath)).RootElement.TryGetProperty("idleBytesPerSecond", out var v))
            {
                _idleBytesPerSecond = v.GetDouble();
            }
        }
        catch { /* forget it - measured again on the next idle upload */ }
    }

    // The line's measured idle upstream, if known (Mbps, for logs/UI).
    public double? IdleMbps => _idleBytesPerSecond is { } b ? b * 8 / 1_000_000 : null;

    // Bytes per second allowed right now; null = unlimited.
    public long? CapBytesPerSecond
    {
        get
        {
            if (!_inGame()) return null;
            if (_config.UploadInGameMbps > 0) return (long)(_config.UploadInGameMbps * 1_000_000 / 8);
            var measured = _idleBytesPerSecond is { } idle ? (long)(idle * InGameShare) : UnmeasuredBytesPerSecond;
            return Math.Max(FloorBytesPerSecond, measured);
        }
    }

    // Writes bytes to the destination, pacing to the current cap and
    // feeding the idle throughput estimate when it runs unthrottled.
    public async Task WriteAsync(Stream destination, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long sent = 0;
        var throttledAtAll = false;
        while (sent < data.Length)
        {
            var cap = CapBytesPerSecond;
            var slice = data.Slice((int)sent, (int)Math.Min(SliceBytes, data.Length - sent));
            await destination.WriteAsync(slice, ct);
            sent += slice.Length;
            if (cap is { } bytesPerSecond)
            {
                throttledAtAll = true;
                var due = TimeSpan.FromSeconds((double)sent / bytesPerSecond);
                var ahead = due - clock.Elapsed;
                if (ahead > TimeSpan.FromMilliseconds(5)) await Task.Delay(ahead, ct);
            }
        }
        if (!throttledAtAll && sent >= 4 * 1024 * 1024 && clock.Elapsed > TimeSpan.FromMilliseconds(500))
        {
            Observe(sent / clock.Elapsed.TotalSeconds);
        }
    }

    // One unthrottled transfer's throughput folds into the idle estimate.
    private void Observe(double bytesPerSecond)
    {
        lock (_gate)
        {
            _idleBytesPerSecond = _idleBytesPerSecond is { } prev
                ? prev + (bytesPerSecond - prev) / SmoothingSamples
                : bytesPerSecond;
            if (DateTime.UtcNow - _lastSavedUtc < TimeSpan.FromMinutes(1)) return;
            _lastSavedUtc = DateTime.UtcNow;
            try
            {
                File.WriteAllText(_memoryPath, JsonSerializer.Serialize(new { idleBytesPerSecond = _idleBytesPerSecond, measuredUtc = DateTime.UtcNow }));
            }
            catch { /* remembered next time */ }
        }
    }
}

// An HTTP body that goes out through the throttle. Content-Length is known
// up front (resumable uploads need it), the bytes leave in paced slices.
public sealed class ThrottledContent(byte[] buffer, int count, UploadThrottle throttle) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
        throttle.WriteAsync(stream, buffer.AsMemory(0, count), CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct) =>
        throttle.WriteAsync(stream, buffer.AsMemory(0, count), ct);

    protected override bool TryComputeLength(out long length)
    {
        length = count;
        return true;
    }
}
