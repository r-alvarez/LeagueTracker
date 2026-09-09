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

public sealed class UploadQuota(DataPaths paths, IOptions<UploadsOptions> options)
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mb = 1024L * 1024;

    public long MaxVodBytes => (long)(options.Value.MaxVodGb * Gb);
    public long MaxRenderBytes => (long)(options.Value.MaxRenderGb * Gb);
    public long MaxClipBytes => options.Value.MaxClipMb * Mb;
    public long MaxSidecarBytes => options.Value.MaxSidecarMb * Mb;

    // A chunked body declares no size: it passes here with 0 and is bounded
    // while it is written.
    public string? Refusal(long incomingBytes)
    {
        if (FreeBytes() - incomingBytes < (long)(options.Value.MinFreeGb * Gb)) return "the tracker's disk is nearly full";
        if (MediaBytes() + incomingBytes > (long)(options.Value.MaxMediaGbPerAccount * Gb)) return $"this account's media allowance ({options.Value.MaxMediaGbPerAccount:0} GB) is used up";
        return null;
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
