using System.Diagnostics;

namespace LeagueTracker.RenderAgent;

public sealed class RenderAgent(AgentConfig config)
{
    private const string GameWindowTitle = "League of Legends (TM) Client";
    private const string GameProcessName = "League of Legends";

    private readonly TrackerClient _tracker = new(config.ServerUrl, config.AgentName);
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "leaguetracker-agent");

    private string _gameDir = "";
    private string _gameExe = "";
    private string _ffmpeg = "";

    /// MockRender skips the game entirely and renders test-pattern clips with
    /// ffmpeg - lets the whole queue/upload pipeline be verified on a machine
    /// without League installed.
    private static bool MockRender => Environment.GetEnvironmentVariable("LT_MOCK_RENDER") is "1" or "true";

    /// Process at most one job and exit - for smoke tests.
    private static bool RunOnce => Environment.GetEnvironmentVariable("LT_ONCE") is "1" or "true";

    public async Task<bool> ValidateAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_workDir);

        if (!await _tracker.PingAsync(ct))
        {
            Log.Error($"Tracker server unreachable at {config.ServerUrl}");
            return false;
        }
        Log.Info("Tracker server reachable");

        _ffmpeg = ResolveFfmpeg();
        if (_ffmpeg is not { Length: > 0 })
        {
            Log.Error("ffmpeg not found - install it (winget install Gyan.FFmpeg) or drop ffmpeg.exe next to the agent");
            return false;
        }
        Log.Info($"ffmpeg: {_ffmpeg}");

        if (MockRender)
        {
            Log.Warn("LT_MOCK_RENDER is on - rendering test patterns instead of the game");
            return true;
        }

        if (ResolveLeague() is not { } league)
        {
            Log.Error("League of Legends install not found - set LeaguePath in appsettings.json");
            return false;
        }
        (_gameDir, _gameExe) = league;
        Log.Info($"League: {_gameExe} (client {InstalledPatch() ?? "unknown"})");

        EnsureReplayApiEnabled();
        return true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"Polling every {config.PollSeconds}s. Ctrl+C to stop.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processedJob = await RunOnceAsync(ct);
                if (processedJob && RunOnce) { Log.Info("LT_ONCE set - exiting after one job"); return; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"Pass failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, config.PollSeconds)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        Log.Info("Stopped.");
    }

    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        // Never fight the player for the machine: skip while they're in a game
        // (server-side knowledge) or while any game client is running locally.
        if (!MockRender)
        {
            if (await _tracker.PlayerInGameAsync(ct)) { Log.Info("Player is in game - waiting"); return false; }
            if (Process.GetProcessesByName(GameProcessName) is { Length: > 0 }) { Log.Info("Game client running - waiting"); return false; }
        }

        var job = await _tracker.ClaimNextAsync(ct);
        if (job is null) return false;

        var windows = config.MaxWindowsPerJob > 0 ? job.Windows.Take(config.MaxWindowsPerJob).ToList() : job.Windows;
        Log.Info($"Job {job.MatchId} ({job.Kind}): {windows.Count} window(s), following \"{job.MyName}\" ({job.MyChampion})");

        try
        {
            if (MockRender) await MockRenderJobAsync(job, windows, ct);
            else await RenderJobAsync(job, windows, ct);

            await _tracker.CompleteAsync(job, ct);
            Log.Info($"Job {job.MatchId} complete");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Job {job.MatchId} failed: {ex.Message}");
            await _tracker.FailAsync(job, ex.Message, CancellationToken.None);
        }
        return true;
    }

    private async Task RenderJobAsync(RenderJob job, List<ClipWindow> windows, CancellationToken ct)
    {
        if (InstalledPatch() is { } client && ParsePatch(job.GameVersion) is { } replay && client != replay)
        {
            throw new InvalidOperationException($"patch mismatch: replay {replay}, client {client} - replay no longer playable");
        }

        var roflPath = Path.Combine(_workDir, $"{job.MatchId}.rofl");
        Log.Info("Downloading replay...");
        await _tracker.DownloadReplayAsync(job, roflPath, ct);

        Process? game = null;
        using var replayApi = new ReplayApiClient();
        try
        {
            Log.Info("Launching replay...");
            game = Process.Start(new ProcessStartInfo(_gameExe, $"\"{roflPath}\"")
            {
                WorkingDirectory = _gameDir,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("could not start the game process");

            await WaitForReplayApiAsync(game, replayApi, ct);
            await replayApi.SetPlaybackAsync(time: null, paused: true, speed: 1, ct);
            if (job.MyName is { Length: > 0 })
            {
                await replayApi.FollowPlayerAsync(job.MyName, ct);
            }

            foreach (var window in windows)
            {
                var output = Path.Combine(_workDir, $"{job.MatchId}-w{window.Index:00}.mp4");
                Log.Info($"Window {window.Index} ({window.Label}, {window.StartSec}-{window.EndSec}s): seeking...");

                await replayApi.SetPlaybackAsync(window.StartSec, paused: true, speed: 1, ct);
                await WaitForSeekAsync(replayApi, window.StartSec, ct);
                // Selection can drop across seeks; re-assert before recording.
                if (job.MyName is { Length: > 0 }) await replayApi.FollowPlayerAsync(job.MyName, ct);
                await replayApi.SetPlaybackAsync(time: null, paused: false, speed: 1, ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                Log.Info($"Window {window.Index}: recording {window.EndSec - window.StartSec}s...");
                await CaptureAsync(output, window.EndSec - window.StartSec, ct);
                await replayApi.SetPlaybackAsync(time: null, paused: true, speed: null, ct);

                await _tracker.UploadAsync(job, window.Index, output, ct);
                File.Delete(output);
                Log.Info($"Window {window.Index}: uploaded");
            }
        }
        finally
        {
            try { if (game is { HasExited: false }) game.Kill(entireProcessTree: true); } catch { /* already gone */ }
            game?.Dispose();
            if (File.Exists(roflPath)) File.Delete(roflPath);
        }
    }

    /// The plumbing-test render: same seek/record/upload rhythm, but the "game"
    /// is an ffmpeg test pattern stamped with the window's in-game clock.
    private async Task MockRenderJobAsync(RenderJob job, List<ClipWindow> windows, CancellationToken ct)
    {
        foreach (var window in windows)
        {
            var output = Path.Combine(_workDir, $"{job.MatchId}-w{window.Index:00}.mp4");
            // Cap mock durations: a mock "full game" only needs to prove the route.
            var duration = Math.Clamp(window.EndSec - window.StartSec, 2, 30);
            // Plain test pattern - drawtext needs fontconfig, which Windows ffmpeg
            // builds crash on; the burnt-in frame counter is enough to eyeball.
            await RunFfmpegAsync($"-y -f lavfi -i testsrc2=size=1280x720:rate=30 -t {duration} -c:v libx264 -preset veryfast -crf 28 -pix_fmt yuv420p \"{output}\"", ct);
            await _tracker.UploadAsync(job, window.Index, output, ct);
            File.Delete(output);
            Log.Info($"Window {window.Index}: mock clip uploaded ({duration}s)");
        }
    }

    private async Task WaitForReplayApiAsync(Process game, ReplayApiClient api, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (game.HasExited) throw new InvalidOperationException($"game exited during load (code {game.ExitCode}) - wrong patch or corrupt replay");
            if (await api.GetPlaybackAsync(ct) is not null) { Log.Info("Replay API up"); return; }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("Replay API did not come up within 3 minutes");
    }

    private static async Task WaitForSeekAsync(ReplayApiClient api, int targetSec, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var playback = await api.GetPlaybackAsync(ct);
            if (playback is { Seeking: false } && Math.Abs(playback.Time - targetSec) < 5) return;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new TimeoutException($"seek to {targetSec}s did not settle");
    }

    private Task CaptureAsync(string output, int durationSec, CancellationToken ct) =>
        RunFfmpegAsync(
            $"-y -f gdigrab -framerate {config.CaptureFramerate} -i title=\"{GameWindowTitle}\" " +
            $"-t {Math.Max(2, durationSec)} -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p \"{output}\"", ct);

    private async Task RunFfmpegAsync(string args, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo(_ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("could not start ffmpeg");

        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 400 ? stderr[^400..] : stderr;
            throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {tail}");
        }
    }

    private string ResolveFfmpeg()
    {
        if (config.FfmpegPath is { Length: > 0 }) return File.Exists(config.FfmpegPath) ? config.FfmpegPath : "";
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        var onPath = Environment.GetEnvironmentVariable("PATH")?.Split(';')
            .Select(dir => Path.Combine(dir.Trim(), "ffmpeg.exe"))
            .FirstOrDefault(File.Exists);
        return onPath ?? "";
    }

    private (string GameDir, string Exe)? ResolveLeague()
    {
        var roots = config.LeaguePath is { Length: > 0 }
            ? [config.LeaguePath]
            : DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed)
                .Select(d => Path.Combine(d.RootDirectory.FullName, "Riot Games", "League of Legends")).ToArray();
        foreach (var root in roots)
        {
            var exe = Path.Combine(root, "Game", "League of Legends.exe");
            if (File.Exists(exe)) return (Path.Combine(root, "Game"), exe);
        }
        return null;
    }

    private string? InstalledPatch() =>
        _gameExe is { Length: > 0 } && File.Exists(_gameExe)
            ? ParsePatch(FileVersionInfo.GetVersionInfo(_gameExe).ProductVersion ?? "")
            : null;

    /// "16.13.791.5903" -> "16.13"; null when the format is unrecognisable.
    private static string? ParsePatch(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _)
            ? $"{parts[0]}.{parts[1]}"
            : null;
    }

    /// One-time setup the Replay API needs; idempotent, and the game only reads
    /// the file at launch so editing while no game runs is safe.
    private void EnsureReplayApiEnabled()
    {
        var cfg = Path.Combine(Path.GetDirectoryName(_gameDir)!, "Config", "game.cfg");
        if (!File.Exists(cfg))
        {
            Log.Warn($"game.cfg not found at {cfg} - enable the Replay API manually (EnableReplayApi=1 under [General])");
            return;
        }

        var lines = File.ReadAllLines(cfg).ToList();
        if (lines.Any(l => l.Trim().StartsWith("EnableReplayApi", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Info("Replay API already enabled in game.cfg");
            return;
        }

        var general = lines.FindIndex(l => l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase));
        if (general < 0) { lines.Add("[General]"); general = lines.Count - 1; }
        lines.Insert(general + 1, "EnableReplayApi=1");
        File.WriteAllLines(cfg, lines);
        Log.Info("Enabled the Replay API in game.cfg");
    }
}
