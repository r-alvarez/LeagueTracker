using System.Collections.Concurrent;
using LeagueTracker.Api.Accounts;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

// `Uploads` section. Postgres on a full disk fails every account at once, so
// one self-enrolled recorder must not be able to fill the NAS (audit D1).
public sealed class UploadsOptions
{
    public double MaxMediaGbPerAccount { get; set; } = 60;
    public double MinFreeGb { get; set; } = 20;
    public double MaxVodGb { get; set; } = 8;
    public double MaxRenderGb { get; set; } = 4;
    public int MaxClipMb { get; set; } = 512;
    public int MaxSidecarMb { get; set; } = 64;
    public int SweepTempAfterHours { get; set; } = 24;
}

public enum UploadRefusal { TooLarge, Allowance, Disk }

// A body without a Content-Length reserves its whole budget, so two
// unknown-length uploads cannot both spend the same last gigabyte.
public sealed class UploadReservation : IDisposable
{
    private readonly Action _release;

    internal UploadReservation(long budget, long fileCap, UploadRefusal? refusal, Action release)
    {
        Budget = budget;
        FileCap = fileCap;
        Refusal = refusal;
        _release = release;
    }

    public long Budget { get; }
    public long FileCap { get; }
    public UploadRefusal? Refusal { get; }

    public UploadRefusal Exceeded => Budget >= FileCap ? UploadRefusal.TooLarge : UploadRefusal.Allowance;

    public void Dispose() => _release();
}

public sealed class UploadQuota(DataPaths paths, IOptions<UploadsOptions> options)
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mb = 1024L * 1024;

    // Reservations outlive a request scope and are shared by every upload to
    // the account, so they live process-wide, keyed by the data folder.
    private static readonly ConcurrentDictionary<string, long> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public long MaxVodBytes => (long)(options.Value.MaxVodGb * Gb);
    public long MaxRenderBytes => (long)(options.Value.MaxRenderGb * Gb);
    public long MaxClipBytes => options.Value.MaxClipMb * Mb;
    public long MaxSidecarBytes => options.Value.MaxSidecarMb * Mb;

    public UploadReservation Reserve(long? declaredBytes, long fileCap)
    {
        lock (Gate)
        {
            var key = paths.DataDir;
            var inFlight = InFlight.GetValueOrDefault(key);
            var allowanceLeft = (long)(options.Value.MaxMediaGbPerAccount * Gb) - MediaBytes() - inFlight;
            var diskLeft = FreeBytes() - (long)(options.Value.MinFreeGb * Gb) - inFlight;
            var budget = Math.Max(0, Math.Min(fileCap, Math.Min(allowanceLeft, diskLeft)));

            var refusal =
                declaredBytes > fileCap ? UploadRefusal.TooLarge
                : diskLeft <= 0 || declaredBytes > diskLeft ? UploadRefusal.Disk
                : allowanceLeft <= 0 || declaredBytes > allowanceLeft ? UploadRefusal.Allowance
                : (UploadRefusal?)null;
            if (refusal is not null) return new UploadReservation(0, fileCap, refusal, () => { });

            var reserved = declaredBytes ?? budget;
            InFlight.AddOrUpdate(key, reserved, (_, current) => current + reserved);
            return new UploadReservation(budget, fileCap, null, () => Release(key, reserved));
        }
    }

    private static void Release(string key, long reserved)
    {
        lock (Gate)
        {
            var left = InFlight.GetValueOrDefault(key) - reserved;
            if (left > 0) InFlight[key] = left; else InFlight.TryRemove(key, out _);
        }
    }

    public static async Task<bool> CopyWithinAsync(Stream body, Stream file, long cap, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long written = 0;
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read is 0) return true;
            if (written + read > cap) return false;
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
        }
    }

    public static string Describe(UploadRefusal refusal) => refusal switch
    {
        UploadRefusal.TooLarge => "the file is larger than this tracker accepts",
        UploadRefusal.Disk => "the tracker's disk is nearly full",
        _ => "this account's media allowance is used up",
    };

    private long FreeBytes()
    {
        Directory.CreateDirectory(paths.DataDir);
        return new DriveInfo(Path.GetFullPath(paths.DataDir)).AvailableFreeSpace;
    }

    // Media is everything the machines send; the raw game JSON under games/
    // is the tracker's own and rebuildable from Riot.
    private long MediaBytes() =>
        !Directory.Exists(paths.DataDir) ? 0 :
        Directory.EnumerateDirectories(paths.DataDir)
            .Where(dir => !Path.GetFileName(dir).Equals("games", StringComparison.OrdinalIgnoreCase))
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Sum(file => new FileInfo(file).Length);
}

// For a reader that parses the body itself (telemetry) and cannot use the
// bounded copy.
public sealed class BudgetedStream(Stream inner, long budget) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        _read += read;
        if (_read > budget) throw new UploadBudgetExceededException();
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        _read += read;
        if (_read > budget) throw new UploadBudgetExceededException();
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed class UploadBudgetExceededException : Exception;

// Interrupted uploads leave .tmp/.part files nobody ever removed; a day is
// longer than any upload the agent retries.
public sealed class TempFileSweeper(AccountRegistry accounts, IOptions<UploadsOptions> options, ILogger<TempFileSweeper> log) : BackgroundService
{
    private static readonly string[] Patterns = ["*.tmp", "*.part", "*.partial"];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            Sweep();
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(options.Value.SweepTempAfterHours);
        var removed = 0;
        foreach (var account in accounts.All.Where(a => Directory.Exists(a.DataDir)))
        {
            foreach (var pattern in Patterns)
            {
                foreach (var file in Directory.EnumerateFiles(account.DataDir, pattern, SearchOption.AllDirectories))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning("Could not remove stale upload {File}: {Message}", file, ex.Message);
                    }
                }
            }
        }
        if (removed > 0) log.LogInformation("Removed {Count} stale upload files", removed);
    }
}
