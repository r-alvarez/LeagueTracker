using System.Diagnostics;

namespace LeagueTracker.RenderAgent;

public sealed class RenderAgent(AgentConfig config)
{
    private const string GameWindowTitle = "League of Legends (TM) Client";
    private const string GameProcessName = "League of Legends";

    private readonly List<TrackerClient> _trackers =
        [.. config.ServerUrls.Select(url => new TrackerClient(url, config.AgentName))];
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

        // The NAS may be rebooting or the stack redeploying when we start (we
        // run at logon) - wait for a tracker rather than giving up.
        while (true)
        {
            var reachable = 0;
            foreach (var tracker in _trackers)
            {
                if (await tracker.PingAsync(ct)) { reachable++; }
                else Log.Warn($"Tracker unreachable: {tracker.ServerUrl} (will keep retrying)");
            }
            if (reachable > 0) { Log.Info($"{reachable}/{_trackers.Count} tracker server(s) reachable"); break; }
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }

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
        // Never fight the player for the machine: skip while ANY tracked account
        // is in a game (server-side knowledge) or a game client runs locally.
        if (!MockRender)
        {
            foreach (var tracker in _trackers)
            {
                try
                {
                    if (await tracker.PlayerInGameAsync(ct)) { Log.Info("Player is in game - waiting"); return false; }
                }
                catch { /* unreachable tracker - checked again next pass */ }
            }
            if (Process.GetProcessesByName(GameProcessName) is { Length: > 0 }) { Log.Info("Game client running - waiting"); return false; }

            // Vanguard only allows replay launches through the League client, so
            // there's no point claiming a job while it's closed.
            if (LcuClient.TryConnect(LeagueRoot) is not { } lcu) { Log.Info("League client not running - waiting"); return false; }
            using (lcu)
            {
                if (!await lcu.IsUpAsync(ct)) { Log.Info("League client still starting - waiting"); return false; }
            }
        }

        foreach (var tracker in _trackers)
        {
            RenderJob? job;
            try
            {
                job = await tracker.ClaimNextAsync(ct);
            }
            catch
            {
                continue;   // tracker down; try the next one
            }
            if (job is not null) return await ProcessJobAsync(tracker, job, ct);
        }
        return false;
    }

    private async Task<bool> ProcessJobAsync(TrackerClient tracker, RenderJob job, CancellationToken ct)
    {

        var windows = config.MaxWindowsPerJob > 0 ? job.Windows.Take(config.MaxWindowsPerJob).ToList() : job.Windows;
        Log.Info($"Job {job.MatchId} ({job.Kind}) from {tracker.ServerUrl}: {windows.Count} window(s), following \"{job.MyName}\" ({job.MyChampion})");

        try
        {
            if (MockRender) await MockRenderJobAsync(tracker, job, windows, ct);
            else await RenderJobAsync(tracker, job, windows, ct);

            await tracker.CompleteAsync(job, ct);
            Log.Info($"Job {job.MatchId} complete");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Job {job.MatchId} failed: {ex.Message}");
            await tracker.FailAsync(job, ex.Message, CancellationToken.None);
        }
        return true;
    }

    private async Task RenderJobAsync(TrackerClient tracker, RenderJob job, List<ClipWindow> windows, CancellationToken ct)
    {
        if (InstalledPatch() is { } client && ParsePatch(job.GameVersion) is { } replay && client != replay)
        {
            throw new InvalidOperationException($"patch mismatch: replay {replay}, client {client} - replay no longer playable");
        }

        // Vanguard denies direct CreateProcess on the game binary, so the launch
        // goes through the League client's replay flow: rofl into its Replays
        // folder (client naming: PLATFORM-gameId), scan, watch.
        var (platform, gameId) = ParseMatchId(job.MatchId);
        using var lcu = LcuClient.TryConnect(LeagueRoot)
            ?? throw new InvalidOperationException("League client not running - it must be open to launch replays under Vanguard");
        var roflPath = Path.Combine(await lcu.GetReplaysPathAsync(ct), $"{platform}-{gameId}.rofl");
        Log.Info("Downloading replay...");
        await tracker.DownloadReplayAsync(job, roflPath, ct);

        Process? game = null;
        using var replayApi = new ReplayApiClient();
        try
        {
            Log.Info("Launching replay through the client...");
            await lcu.ScanAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await lcu.WatchAsync(gameId, ct);
            game = await WaitForGameProcessAsync(ct);

            await WaitForReplayApiAsync(game, replayApi, ct);
            await replayApi.SetPlaybackAsync(time: null, paused: true, speed: 1, ct);
            var cameraName = await ResolveCameraNameAsync(replayApi, job, ct);

            foreach (var window in windows)
            {
                var output = Path.Combine(_workDir, $"{job.MatchId}-w{window.Index:00}.mp4");
                Log.Info($"Window {window.Index} ({window.Label}, {window.StartSec}-{window.EndSec}s): seeking...");

                await replayApi.SetPlaybackAsync(window.StartSec, paused: true, speed: 1, ct);
                await WaitForSeekAsync(replayApi, window.StartSec, ct);
                // Selection can drop across seeks; re-assert before recording.
                if (cameraName is { Length: > 0 }) await replayApi.FollowPlayerAsync(cameraName, ct);
                await replayApi.SetPlaybackAsync(time: null, paused: false, speed: 1, ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                Log.Info($"Window {window.Index}: recording {window.EndSec - window.StartSec}s...");
                await CaptureAsync(output, window.EndSec - window.StartSec, ct);
                await replayApi.SetPlaybackAsync(time: null, paused: true, speed: null, ct);

                await tracker.UploadAsync(job, window.Index, output, ct);
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
    private async Task MockRenderJobAsync(TrackerClient tracker, RenderJob job, List<ClipWindow> windows, CancellationToken ct)
    {
        foreach (var window in windows)
        {
            var output = Path.Combine(_workDir, $"{job.MatchId}-w{window.Index:00}.mp4");
            // Cap mock durations: a mock "full game" only needs to prove the route.
            var duration = Math.Clamp(window.EndSec - window.StartSec, 2, 30);
            // Plain test pattern - drawtext needs fontconfig, which Windows ffmpeg
            // builds crash on; the burnt-in frame counter is enough to eyeball.
            await RunFfmpegAsync($"-y -f lavfi -i testsrc2=size=1280x720:rate=30 -t {duration} -c:v libx264 -preset veryfast -crf 28 -pix_fmt yuv420p \"{output}\"", ct);
            await tracker.UploadAsync(job, window.Index, output, ct);
            File.Delete(output);
            Log.Info($"Window {window.Index}: mock clip uploaded ({duration}s)");
        }
    }

    private string LeagueRoot => Path.GetDirectoryName(_gameDir)!;

    /// selectionName only takes a name exactly as the game knows it, and Riot
    /// ID formats vary - so try what the game itself reports for the tracked
    /// player (champion matches first) and keep the first that verifiably
    /// sticks. Falls back to the server-sent name with a warning.
    private static async Task<string?> ResolveCameraNameAsync(ReplayApiClient api, RenderJob job, CancellationToken ct)
    {
        foreach (var name in await api.GetCameraCandidatesAsync(job.MyName, job.MyChampion, ct))
        {
            await api.FollowPlayerAsync(name, ct);
            if (string.Equals(await api.GetSelectionAsync(ct), name, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Camera locked on \"{name}\"");
                return name;
            }
        }
        if (job.MyName is { Length: > 0 })
        {
            Log.Warn($"Could not verify a camera lock for \"{job.MyName}\" - using it unverified");
            await api.FollowPlayerAsync(job.MyName, ct);
        }
        return job.MyName;
    }

    /// "EUW1_7913572469" -> ("EUW1", 7913572469).
    private static (string Platform, long GameId) ParseMatchId(string matchId)
    {
        var parts = matchId.Split('_');
        return parts.Length == 2 && long.TryParse(parts[1], out var gameId)
            ? (parts[0], gameId)
            : throw new InvalidOperationException($"unexpected match id format: {matchId}");
    }

    /// The client starts the game on its own schedule after watch; wait for it.
    private static async Task<Process> WaitForGameProcessAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var procs = Process.GetProcessesByName(GameProcessName);
            if (procs.Length > 0)
            {
                foreach (var extra in procs[1..]) extra.Dispose();
                return procs[0];
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new TimeoutException("the client did not start the replay within 90s");
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

    private Task CaptureAsync(string output, int durationSec, CancellationToken ct)
    {
        // Desktop Duplication cropped to the game window - gdigrab's window
        // capture is black for Direct3D content.
        var rect = GameWindow.FindClientRect(GameWindowTitle)
            ?? throw new InvalidOperationException($"game window \"{GameWindowTitle}\" not found for capture");
        var width = rect.Width & ~1;    // yuv420p needs even dimensions
        var height = rect.Height & ~1;
        return RunFfmpegAsync(
            $"-y -f lavfi -i ddagrab=framerate={config.CaptureFramerate}:offset_x={Math.Max(0, rect.X)}:offset_y={Math.Max(0, rect.Y)}:video_size={width}x{height} " +
            $"-vf hwdownload,format=bgra -t {Math.Max(2, durationSec)} -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p \"{output}\"", ct);
    }

    private async Task RunFfmpegAsync(string args, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo(_ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            // The agent runs windowless; without this ffmpeg would pop a console.
            CreateNoWindow = true,
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
