using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace LeagueTracker.RenderAgent;

/// The housekeeping loop that makes an agent survivable on a machine nobody
/// administers: announces itself to every tracker each poll, re-reads the
/// trackers' agent profile hourly (a rotated YouTube token reaches every
/// machine without anyone touching it), and installs published builds by
/// itself - through the same stop.requested handshake a manual deploy uses,
/// so an in-flight render postpones and a recording finalizes first.
public sealed class AgentSupervisor(AgentConfig config, IReadOnlyList<TrackerClient> trackers)
{
    private const string GameProcessName = "League of Legends";
    private static readonly TimeSpan ProfileEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(1);

    private DateTime _lastProfile = DateTime.MinValue;
    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private readonly HashSet<string> _failedVersions = [];

    public static string UpdateDir => Path.Combine(AppContext.BaseDirectory, "update");

    /// First profile pass, before the loops start: the recorder validates its
    /// YouTube credentials at startup, and those may only exist server-side.
    public async Task ApplyProfileAsync(CancellationToken ct)
    {
        _lastProfile = DateTime.UtcNow;
        var applied = new List<string>();
        foreach (var tracker in trackers)
        {
            if (await tracker.GetProfileAsync(ct) is not { Count: > 0 } profile) continue;
            applied.AddRange(config.ApplyProfile(profile));
        }
        if (applied is { Count: > 0 }) Log.Info($"Profile from tracker applied: {string.Join(", ", applied.Distinct())}");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (RenderAgent.StopRequested) return;
            try
            {
                var latestHint = await HeartbeatAsync(ct);
                if (DateTime.UtcNow - _lastProfile > ProfileEvery) await ApplyProfileAsync(ct);
                var newerAdvertised = latestHint is { } hint && Version.TryParse(hint, out var v) && v > CurrentVersion;
                if (newerAdvertised || DateTime.UtcNow - _lastUpdateCheck > UpdateCheckEvery)
                {
                    _lastUpdateCheck = DateTime.UtcNow;
                    if (await TryUpdateAsync(ct)) return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Supervisor pass failed: {ex.Message}");
            }
            // Sliced so a tray Quit or a deploy's sentinel is honoured within
            // seconds, not at the end of a full poll interval.
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(15, config.PollSeconds));
            try
            {
                while (DateTime.UtcNow < until && !RenderAgent.StopRequested) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// Manual "check for updates" from the tray: same rules as the hourly
    /// pass, just now.
    public Task<bool> CheckForUpdateAsync(CancellationToken ct) => TryUpdateAsync(ct);

    private static Version CurrentVersion => Version.TryParse(AgentConfig.Version, out var v) ? v : new Version(0, 0);

    private async Task<string?> HeartbeatAsync(CancellationToken ct)
    {
        var (state, detail) = AgentStatus.Current;
        var beat = new
        {
            Agent = config.AgentName,
            AgentConfig.Version,
            config.Role,
            RenderAgent.Paused,
            State = RenderAgent.Paused && state is "idle" ? "paused" : state,
            Detail = detail,
            AgentStatus.LastRecordingUtc,
            AgentStatus.YouTubeReady,
            AgentStatus.LastError,
            Machine = Environment.MachineName,
            User = Environment.UserName,
        };
        string? latest = null;
        foreach (var tracker in trackers)
        {
            latest ??= await tracker.HeartbeatAsync(beat, ct);
        }
        return latest;
    }

    /// True when an update was staged and the agent must now exit (the
    /// stop sentinel is in place; the apply script waits for the process).
    private async Task<bool> TryUpdateAsync(CancellationToken ct)
    {
        AgentRelease? best = null;
        TrackerClient? source = null;
        foreach (var tracker in trackers)
        {
            if (await tracker.GetReleaseAsync(ct) is not { } release || !Version.TryParse(release.Version, out var v)) continue;
            if (best is null || v > Version.Parse(best.Version)) (best, source) = (release, tracker);
        }
        if (best is null || source is null) return false;
        // 0.0.0.0 is a dev build run from bin/ - it must never be replaced by
        // whatever the trackers publish.
        if (CurrentVersion.Major is 0 || Version.Parse(best.Version) <= CurrentVersion || _failedVersions.Contains(best.Version)) return false;

        // Only between things: never under a game (live or replay) or a
        // recording/upload in progress - those finish on their own schedule
        // and the next pass tries again.
        if (Process.GetProcessesByName(GameProcessName) is { Length: > 0 }) return false;
        if (AgentStatus.Current.State is not ("idle" or "paused")) return false;

        Log.Info($"Agent {best.Version} is published (running {AgentConfig.Version}) - updating from {source.ServerUrl}");
        var staging = Path.Combine(Path.GetTempPath(), "leaguetracker-agent");
        Directory.CreateDirectory(staging);
        var zipPath = Path.Combine(staging, best.File);
        try
        {
            await source.DownloadReleaseAsync(best, zipPath, ct);
            await using (var stream = File.OpenRead(zipPath))
            {
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                if (!sha.Equals(best.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"sha256 mismatch (got {sha[..12]}…, expected {best.Sha256[..12]}…)");
                }
            }
            if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, recursive: true);
            Directory.CreateDirectory(UpdateDir);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    // The user's settings and tokens are theirs; the build
                    // never overwrites them.
                    if (entry.Name is "" or "appsettings.json" or "youtube-token.json") continue;
                    entry.ExtractToFile(Path.Combine(UpdateDir, entry.Name), overwrite: true);
                }
            }
            if (!File.Exists(Path.Combine(UpdateDir, "LeagueTracker.RenderAgent.exe")))
            {
                throw new InvalidOperationException("the zip holds no LeagueTracker.RenderAgent.exe");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failedVersions.Add(best.Version);
            Log.Error($"Update to {best.Version} failed: {ex.Message} - staying on {AgentConfig.Version} (retried at the next published version)");
            AgentStatus.LastError = $"update {best.Version}: {ex.Message}";
            return false;
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* staging leftovers are harmless */ }
        }

        LaunchApplyScript(staging);
        File.WriteAllText(RenderAgent.StopSentinelPath, $"self-update to {best.Version}");
        AgentStatus.Set("updating", best.Version);
        Log.Info($"Update {best.Version} staged - stopping so it can be applied");
        return true;
    }

    /// A detached cmd that outlives this process: waits for our PID to go,
    /// swaps the staged files in (previous build kept as *.prev, the way
    /// manual deploys do), and relaunches. Written outside UpdateDir so it
    /// can delete that folder when done.
    private static void LaunchApplyScript(string staging)
    {
        var script = Path.Combine(staging, "apply-update.cmd");
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LeagueTracker.RenderAgent.exe");
        var lines = new[]
        {
            "@echo off",
            "setlocal",
            $"set PID={Environment.ProcessId}",
            ":wait",
            "tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul",
            "if not errorlevel 1 (",
            "  ping -n 3 127.0.0.1 >nul",
            "  goto wait",
            ")",
            $"cd /d \"{AppContext.BaseDirectory.TrimEnd('\\')}\"",
            "for %%f in (update\\*) do (",
            "  if exist \"%%~nxf.prev\" del /f /q \"%%~nxf.prev\"",
            "  if exist \"%%~nxf\" ren \"%%~nxf\" \"%%~nxf.prev\"",
            "  move /y \"%%f\" \"%%~nxf\" >nul",
            ")",
            "rmdir /s /q update",
            "del /f /q stop.requested 2>nul",
            $"start \"\" \"{exe}\"",
        };
        File.WriteAllLines(script, lines);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = staging,
        });
    }
}
