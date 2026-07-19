using System.Diagnostics;

namespace LeagueTracker.RenderAgent;

public sealed class RenderAgent(AgentConfig config)
{
    private const string GameWindowTitle = "League of Legends (TM) Client";
    private const string GameProcessName = "League of Legends";

    private readonly List<TrackerClient> _trackers =
        [.. config.ServerUrls.Select(url => new TrackerClient(url, config))];
    private readonly HashSet<string> _reportedClaimFailures = [];
    // Postpone history per (tracker, job): a reason that repeats identically
    // is a deterministic failure wearing a transient's clothes.
    private readonly Dictionary<string, (string Reason, int Count)> _postpones = [];
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "leaguetracker-agent");

    private string _gameDir = "";
    private string _gameExe = "";
    private string _ffmpeg = "";
    private bool _reportedUserActive;

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
        // Never fight the player for the machine - judged from THIS machine
        // only: a game client running locally, or the local League client
        // anywhere in the play flow (lobby, queue, champ select, loading,
        // in game). Tracked accounts playing elsewhere don't need this PC.
        if (!MockRender)
        {
            if (Process.GetProcessesByName(GameProcessName) is { Length: > 0 }) { Log.Info("Game client running - waiting"); return false; }

            // Vanguard only allows replay launches through the League client, so
            // there's no point claiming a job while it's closed.
            if (LcuClient.TryConnect(LeagueRoot) is not { } lcu) { Log.Info("League client not running - waiting"); return false; }
            using (lcu)
            {
                if (!await lcu.IsUpAsync(ct)) { Log.Info("League client still starting - waiting"); return false; }
                // Unknown phases block too: the safe default is to assume the
                // player is (about to be) playing. None = idle in the client;
                // WatchInProgress = a replay, which the process check already
                // covers when one is really running.
                if (await lcu.GetGameflowPhaseAsync(ct) is { Length: > 0 } phase
                    and not ("None" or "WatchInProgress" or "TerminatedInError"))
                {
                    Log.Info($"Player is in {phase} on this machine - waiting");
                    return false;
                }
            }

            // Idle gate: the camera lock needs the game window focused, which
            // can only be taken reliably (and politely) when nobody is using
            // the PC - so renders wait until the keyboard/mouse go quiet.
            if (GameWindow.UserIdleTime < TimeSpan.FromSeconds(config.IdleSeconds))
            {
                if (!_reportedUserActive) Log.Info($"User is active - rendering waits for {config.IdleSeconds}s of idle");
                _reportedUserActive = true;
                return false;
            }
            _reportedUserActive = false;
        }

        foreach (var tracker in _trackers)
        {
            RenderJob? job;
            try
            {
                job = await tracker.ClaimNextAsync(ct);
                _reportedClaimFailures.Remove(tracker.ServerUrl);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Down or misbehaving tracker - try the next one, but say so:
                // a swallowed claim failure looks exactly like an empty queue.
                // Warned once per outage, or every poll would repeat it.
                if (_reportedClaimFailures.Add(tracker.ServerUrl))
                {
                    Log.Warn($"Claiming from {tracker.ServerUrl} failed: {ex.Message} (not repeated until it recovers)");
                }
                continue;
            }
            if (job is not null) return await ProcessJobAsync(tracker, job, ct);
        }
        return false;
    }

    private async Task<bool> ProcessJobAsync(TrackerClient tracker, RenderJob job, CancellationToken ct)
    {

        var windows = config.MaxWindowsPerJob > 0 ? job.Windows.Take(config.MaxWindowsPerJob).ToList() : job.Windows;
        Log.Info($"Job {job.MatchId} ({job.Kind}) from {tracker.ServerUrl}: {windows.Count} window(s), following \"{job.MyName}\" ({job.MyChampion})");

        var postponeKey = $"{tracker.ServerUrl}|{job.Kind}:{job.MatchId}";
        try
        {
            if (MockRender) await MockRenderJobAsync(tracker, job, windows, ct);
            else await RenderJobAsync(tracker, job, windows, ct);

            await tracker.CompleteAsync(job, ct);
            _postpones.Remove(postponeKey);
            Log.Info($"Job {job.MatchId} complete");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RenderPostponedException ex)
        {
            var count = _postpones.TryGetValue(postponeKey, out var prior) && prior.Reason == ex.Message
                ? prior.Count + 1 : 1;
            _postpones[postponeKey] = (ex.Message, count);
            if (count >= MaxIdenticalPostpones)
            {
                // The same reason this many times running is deterministic,
                // not transient - fail so it surfaces on the Data page (where
                // retry is a click) instead of recycling on every lease expiry.
                _postpones.Remove(postponeKey);
                Log.Error($"Job {job.MatchId} failed: postponed {count} times with the same reason: {ex.Message}");
                await tracker.FailAsync(job, $"postponed {count} times with the same reason - {ex.Message}", CancellationToken.None);
            }
            else
            {
                Log.Warn($"Job {job.MatchId} postponed ({count}/{MaxIdenticalPostpones} for this reason): {ex.Message} - retried automatically when the lease expires (~30 min)");
            }
        }
        catch (Exception ex)
        {
            _postpones.Remove(postponeKey);
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
        // Directed camera OFF is load-bearing for verification: with it off,
        // nothing tracks the action by itself, so a moving camera can only
        // mean the champion lock engaged. (The dropdown's champion entries
        // remain available either way - verified empirically.)
        EnsureDirectedCameraDisabled();

        using var lcu = LcuClient.TryConnect(LeagueRoot)
            ?? throw new InvalidOperationException("League client not running - it must be open to launch replays under Vanguard");
        var roflPath = Path.Combine(await lcu.GetReplaysPathAsync(ct), $"{platform}-{gameId}.rofl");
        Log.Info("Downloading replay...");
        await tracker.DownloadReplayAsync(job, roflPath, ct);

        Process? game = null;
        string? cameraName = null;
        using var replayApi = new ReplayApiClient();

        async Task<Process> StartReplayAsync()
        {
            Log.Info("Launching replay through the client...");
            await lcu.ScanAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await lcu.WatchAsync(gameId, ct);
            var proc = await WaitForGameProcessAsync(ct);
            await WaitForReplayApiAsync(proc, replayApi, ct);
            await replayApi.SetPlaybackAsync(time: null, paused: true, speed: 1, ct);
            // UI assert (target frame, no side frames, fog flag) - once per
            // game process; a relaunch redoes it because the fresh process
            // starts from the persisted UI state again.
            cameraName = await ResolveCameraNameAsync(replayApi, job, ct);
            return proc;
        }

        // Only for a hung process: the Replay API keeps answering on one, so
        // nothing short of a fresh process gives a recording a real retry.
        async Task RestartReplayAsync()
        {
            try { if (game is { HasExited: false }) game.Kill(entireProcessTree: true); } catch { /* already gone */ }
            game?.Dispose();
            game = null;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            game = await StartReplayAsync();
        }

        try
        {
            game = await StartReplayAsync();

            // The camera/fog dropdowns are clicked PER WINDOW, after its seek:
            // any seek that rewinds (and the engage verification always plays
            // past the point it must return to) reloads the world and silently
            // wipes the dropdown state, so it must be re-applied as the last
            // step before every recording. Each window seeks to a pre-roll a
            // few seconds early, engages, verifies tracking during the pre-roll
            // (trustworthy because the directed camera is off - nothing else
            // tracks), and rolls into the recording without further seeks.
            // Slot resolution distinguishes "can't see the list yet" (the list
            // can lag the playback API while the game loads - postpone) from
            // "the list is there but the champion isn't" (bad camera-target
            // data that no amount of retrying fixes - fail so it surfaces
            // instead of recycling on every lease expiry forever).
            if (job.MyChampion is not { Length: > 0 } myChampion)
            {
                throw new InvalidOperationException("no camera target for this match - the tracked player's participant row is missing");
            }
            List<(string Champion, bool Blue)> players = [];
            for (var attempt = 1; attempt <= 5 && players.Count == 0; attempt++)
            {
                players = await replayApi.GetPlayerListAsync(ct);
                if (players.Count == 0) await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            if (players.Count == 0)
            {
                throw new RenderPostponedException("the replay's player list did not come up");
            }
            var slotIndex = players.FindIndex(p => ReplayApiClient.ChampionMatches(p.Champion, myChampion));
            if (slotIndex < 0)
            {
                throw new InvalidOperationException(
                    $"\"{myChampion}\" is not in the replay's player list ({string.Join(", ", players.Select(p => p.Champion))})");
            }
            var slot = (Index: slotIndex, Blue: players[slotIndex].Blue);

            var skippedWindows = new List<int>();
            foreach (var window in windows)
            {
                var output = Path.Combine(_workDir, $"{job.MatchId}-w{window.Index:00}.mp4");
                var duration = Math.Max(2, window.EndSec - window.StartSec);
                var preRoll = Math.Max(0, window.StartSec - EngagePreRollSec);
                var engaged = false;
                var frozen = 0;
                var skippedThis = false;
                for (var attempt = 1; ; attempt++)
                {
                    Log.Info($"Window {window.Index} ({window.Label}, {window.StartSec}-{window.EndSec}s): seeking...");
                    await replayApi.SetPlaybackAsync(preRoll, paused: true, speed: 1, ct);
                    await WaitForSeekAsync(replayApi, preRoll, ct);
                    await replayApi.SetPlaybackAsync(time: null, paused: false, speed: 1, ct);

                    engaged = await EngageCameraAsync(replayApi, slot, attempt, cameraName, ct);
                    if (!engaged)
                    {
                        if (attempt >= 3) break;
                        Log.Warn($"Window {window.Index}: camera did not engage - retrying");
                        continue;
                    }

                    Log.Info($"Window {window.Index}: recording {duration}s...");
                    var started = DateTime.UtcNow;
                    await CaptureAsync(output, duration, ct);
                    await replayApi.SetPlaybackAsync(time: null, paused: true, speed: null, ct);

                    // Desktop Duplication can end the stream early (e.g. a display
                    // mode switch) with ffmpeg still exiting 0 - trust the wall
                    // clock, not the exit code, and redo the window.
                    var recorded = (DateTime.UtcNow - started).TotalSeconds;
                    if (recorded < duration - 3 && attempt < 3)
                    {
                        Log.Warn($"Window {window.Index}: capture ended after {recorded:0}s of {duration}s - retrying");
                        continue;
                    }

                    // A hung replay keeps rendering frames (and the Replay API
                    // keeps answering, seeks "settling" and all) while the
                    // simulation is stuck - every API-side check passes and the
                    // capture is a still image. The rendered game clock is the
                    // ground truth the API can't fake. A hung game never
                    // recovers, so the retry needs a fresh process; a window
                    // that hangs the fresh process too has a cursed timestamp
                    // in this .rofl - skip it so the remaining windows render,
                    // and the job-end failure names it.
                    if (await SimFrozeDuringAsync(output, ct))
                    {
                        frozen++;
                        if (frozen >= 2)
                        {
                            skippedWindows.Add(window.Index);
                            skippedThis = true;
                            Log.Warn($"Window {window.Index}: the simulation hung again on a fresh game process - skipping this window");
                            await RestartReplayAsync();
                            break;
                        }
                        Log.Warn($"Window {window.Index}: the game clock froze during recording - relaunching the replay to retry the window");
                        await RestartReplayAsync();
                        continue;
                    }
                    break;
                }
                if (skippedThis)
                {
                    if (File.Exists(output)) File.Delete(output);
                    continue;
                }
                if (!engaged)
                {
                    throw new RenderPostponedException("the camera did not engage (user active?)");
                }

                await tracker.UploadAsync(job, window.Index, output, ct);
                File.Delete(output);
                Log.Info($"Window {window.Index}: uploaded");
            }
            if (skippedWindows.Count > 0)
            {
                // Partial coverage must not read as complete: fail with the
                // skipped windows named, so the gap is visible on the Data
                // page next to the clips that did upload.
                throw new InvalidOperationException(
                    $"window(s) {string.Join(", ", skippedWindows)} skipped - the replay simulation hangs at their recordings; every other window uploaded");
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
        // The player list can lag the playback API by a few seconds while the
        // game finishes loading - retry before giving up on a verified name.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            foreach (var name in await api.GetCameraCandidatesAsync(job.MyName, job.MyChampion, ct))
            {
                await api.FollowPlayerAsync(name, ct);
                if (string.Equals(await api.GetSelectionAsync(ct), name, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"Selected \"{name}\" (fog + target frame follow the selection)");
                    return name;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        // Unverified names must not reach the Y toggle - without a selection it
        // flips the replay into the directed camera. Fog still gets a chance.
        if (job.MyName is { Length: > 0 })
        {
            Log.Warn($"Could not verify a selection for \"{job.MyName}\" - recording with a free camera");
            await api.FollowPlayerAsync(job.MyName, ct);
        }
        return null;
    }

    /// "EUW1_7913572469" -> ("EUW1", 7913572469).
    private static (string Platform, long GameId) ParseMatchId(string matchId)
    {
        var parts = matchId.Split('_');
        return parts.Length == 2 && long.TryParse(parts[1], out var gameId)
            ? (parts[0], gameId)
            : throw new InvalidOperationException($"unexpected match id format: {matchId}");
    }

    // Replay UI geometry as ratios of the client area, calibrated at 2560x1440
    // with default HUD scale (GlobalScaleReplay=1). The camera dropdown lists
    // 13 entries (FPS, Directed, Manual, then the 10 champions in player-list
    // order) stacked upward from the box; the fog dropdown lists Blue/Red/All.
    private const double PanelX = 0.0703;
    private const double CameraBoxY = 0.9167;
    private const double CameraListBottomY = 0.90625;
    private const double DropdownRowH = 0.021806;
    private const double FogX = 0.114;
    private const double FogBoxY = 0.948;
    private const double FogBlueY = 0.8813;
    private const double FogRedY = 0.9035;

    private const int EngagePreRollSec = 10;
    private const int MaxIdenticalPostpones = 3;

    /// The lock check measures distance from a parked reference point. It
    /// rotates per attempt: a fight can coincide with one park spot (making a
    /// real lock look unengaged when the champion also stands still), but it
    /// cannot coincide with all of them, so the coincidence never repeats on
    /// every attempt of every lease.
    private static readonly (double X, double Z)[] CameraParkSpots =
    [
        (12600, 12600),
        (1800, 12800),
        (12800, 1800),
    ];

    /// Clicks the tracked champion in the camera dropdown and their side in
    /// the fog dropdown, then verifies the camera really tracks - the Replay
    /// API has no working equivalent, and the verification cannot
    /// false-positive while the directed camera is disabled. Runs while the
    /// replay is playing (the lock only engages during playback).
    private async Task<bool> EngageCameraAsync(ReplayApiClient replayApi, (int Index, bool Blue) slot, int attempt, string? cameraName, CancellationToken ct)
    {
        // Park the free camera away from where the world-reload leaves it,
        // BEFORE the clicks (render-API writes reset the camera mode). The
        // park re-asserts the selection too, in case a prior write cleared it.
        var spot = CameraParkSpots[(attempt - 1) % CameraParkSpots.Length];
        var parked = await replayApi.ParkCameraAsync(spot.X, 1911, spot.Z, cameraName, ct);

        if (!GameWindow.TryClickAt(GameWindowTitle, PanelX, CameraBoxY))
        {
            Log.Warn("Could not focus the game window for the camera dropdown");
            return false;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
        var championRowY = CameraListBottomY - (10 - slot.Index) * DropdownRowH + DropdownRowH / 2;
        GameWindow.TryClickAt(GameWindowTitle, PanelX, championRowY);
        // The game hit-tests against the live cursor on the next frame, so
        // moving the cursor in the same instant as the click voids it.
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        GameWindow.TryMoveCursor(GameWindowTitle, 0.5, 0.35);

        // Camera verification first - its ~5s doubles as settle time for the
        // freshly-initialized UI, which made a fog click right after the
        // camera clicks miss on the session's first window.
        if (!await CameraTracksAsync(replayApi, parked, ct))
        {
            // Which failure is it? Camera still at the park = clicks never
            // landed (or lock has no effect); moved but near the park =
            // stationary champion near the reference; empty selection = a
            // write cleared it. The loops this diagnoses are rare enough
            // that the extra API reads don't matter.
            var current = await replayApi.GetCameraPositionAsync(ct);
            var selection = await replayApi.GetSelectionAsync(ct);
            Log.Warn($"Camera check failed: parked=({parked?.X:0},{parked?.Z:0}) now=({current?.X:0},{current?.Z:0}) selection='{selection}'");
            return false;
        }

        // Fog perspective: the dropdown defaults to All (no fog); pick the
        // tracked player's side. Deterministic click, no readback available,
        // idempotent when already set.
        if (GameWindow.TryClickAt(GameWindowTitle, FogX, FogBoxY))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
            GameWindow.TryClickAt(GameWindowTitle, FogX, slot.Blue ? FogBlueY : FogRedY);
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        }

        // Park the cursor away from the panel and screen edges so it neither
        // shows over the HUD in recordings nor edge-scrolls the camera.
        GameWindow.TryMoveCursor(GameWindowTitle, 0.5, 0.35);
        return true;
    }

    /// Not a failure: the conditions for a quality render weren't met (camera
    /// lock, selection). The job is left unfailed so it becomes claimable
    /// again when its lease expires, instead of needing a manual retry.
    private sealed class RenderPostponedException(string message) : Exception(message);

    private static async Task<bool> CameraTracksAsync(ReplayApiClient api, (double X, double Z)? reference, CancellationToken ct)
    {
        // A locked camera snaps to the champion; an unlocked one stays where
        // it was parked. Distance from the park reference is therefore a lock
        // signal that works even while the champion stands still; movement
        // between samples is the fallback for a fight that happens to sit
        // near the reference (which the rotating park spots keep from
        // repeating on every attempt). Reference falls back to the
        // world-reload corner when parking couldn't be read back.
        var (refX, refZ) = reference ?? (DefaultCameraX, DefaultCameraZ);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var a = await api.GetCameraPositionAsync(ct);
        if (a is { } snap && Math.Abs(snap.X - refX) + Math.Abs(snap.Z - refZ) > 1500) return true;
        await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
        var b = await api.GetCameraPositionAsync(ct);
        return a is { } pa && b is { } pb && Math.Abs(pa.X - pb.X) + Math.Abs(pa.Z - pb.Z) > 75;
    }

    private const double DefaultCameraX = 300;
    private const double DefaultCameraZ = -770;

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
        var rect = GameWindow.FindClientRect(GameWindowTitle);
        if (rect is not { Width: >= 32, Height: >= 32 })
        {
            // Minimized windows report a degenerate rect; bring it back.
            GameWindow.TryRestore(GameWindowTitle);
            rect = GameWindow.FindClientRect(GameWindowTitle);
        }
        if (rect is not { Width: >= 32, Height: >= 32 } r)
        {
            throw new InvalidOperationException("game window not found or minimized - the replay must stay visible while recording");
        }
        var width = r.Width & ~1;    // yuv420p needs even dimensions
        var height = r.Height & ~1;
        return RunFfmpegAsync(
            $"-y -f lavfi -i ddagrab=framerate={config.CaptureFramerate}:offset_x={Math.Max(0, r.X)}:offset_y={Math.Max(0, r.Y)}:video_size={width}x{height} " +
            $"-vf hwdownload,format=bgra -t {Math.Max(2, durationSec)} -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p \"{output}\"", ct);
    }

    /// True when the in-game clock (top-centre of the HUD, ticks every second
    /// while the simulation runs) sits unchanged for 5s+ anywhere in the clip.
    /// Cropping to the clock avoids false life from things that animate even
    /// when the sim is hung: torch flames, water, the FPS counter, the cursor.
    /// Calibrated against real clips: a hung job's clips freeze wall-to-wall
    /// (bar one keyframe pulse); healthy clips report nothing.
    private async Task<bool> SimFrozeDuringAsync(string clipPath, CancellationToken ct)
    {
        var stderr = await RunFfmpegAsync(
            $"-i \"{clipPath}\" -vf \"crop=in_w*0.08:in_h*0.05:in_w*0.45:in_h*0.05,freezedetect=n=0.003:d=5\" -an -f null -", ct);
        return stderr.Contains("freeze_start", StringComparison.Ordinal);
    }

    private async Task<string> RunFfmpegAsync(string args, CancellationToken ct)
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
        return stderr;
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

    /// The game persists EnableDirectedCamera in game.cfg's [Replay] section and
    /// reads it at launch. Idempotent; called while no game runs.
    private void EnsureDirectedCameraDisabled()
    {
        var cfg = Path.Combine(LeagueRoot, "Config", "game.cfg");
        if (!File.Exists(cfg)) return;

        var lines = File.ReadAllLines(cfg).ToList();
        var existing = lines.FindIndex(l => l.Trim().StartsWith("EnableDirectedCamera", StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            if (lines[existing].Trim().EndsWith("=0")) return;
            lines[existing] = "EnableDirectedCamera=0";
        }
        else
        {
            var replay = lines.FindIndex(l => l.Trim().Equals("[Replay]", StringComparison.OrdinalIgnoreCase));
            if (replay < 0) { lines.Add("[Replay]"); replay = lines.Count - 1; }
            lines.Insert(replay + 1, "EnableDirectedCamera=0");
        }
        File.WriteAllLines(cfg, lines);
        Log.Info("Disabled the directed replay camera in game.cfg");
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
