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
    private int _orphanStrikes;
    private DateTime _lastClientLaunchAttempt = DateTime.MinValue;
    private bool _clientLaunchUnavailable;

    /// Deploys ask the agent to stop by dropping this file next to the exe:
    /// the agent finishes (or cleanly postpones) what it is doing and exits,
    /// instead of being hard-killed mid-render - which leaves an orphaned
    /// replay process behind and loses the claimed job until lease expiry.
    public static string StopSentinelPath => Path.Combine(AppContext.BaseDirectory, "stop.requested");
    public static bool StopRequested => File.Exists(StopSentinelPath);

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

        // A previous incarnation of this agent may have died holding leases
        // (crash, hard kill mid-render) - free them so those jobs re-queue
        // now rather than after the 30-minute lease.
        foreach (var tracker in _trackers)
        {
            if (await tracker.ReleaseStaleLeasesAsync(ct) is { Count: > 0 } released)
            {
                Log.Info($"Released stale lease(s) on {tracker.ServerUrl}: {string.Join(", ", released)}");
            }
        }

        _ffmpeg = ResolveFfmpeg(config);
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
            if (StopRequested) { Log.Info("stop.requested found - render loop exiting"); return; }
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
        // A between-games review owns the replay client while it runs. This
        // has to come FIRST, ahead of the orphan sweep below: a review's
        // replay is API-launched, so gameflow reads "None", and a player
        // sitting still to think about a moment is indistinguishable from an
        // idle machine - the orphan rule would kill the very replay they are
        // watching. Rendering simply waits for the review to end.
        if (ReplayReview.SessionActive)
        {
            if (!_reportedUserActive) Log.Info("Post-game review in progress - rendering waits for it to finish");
            _reportedUserActive = true;
            _orphanStrikes = 0;
            return false;
        }

        // Never fight the player for the machine - judged from THIS machine
        // only: a game client running locally, or the local League client
        // anywhere in the play flow (lobby, queue, champ select, loading,
        // in game). Tracked accounts playing elsewhere don't need this PC.
        if (!MockRender)
        {
            if (Process.GetProcessesByName(GameProcessName) is { Length: > 0 } games)
            {
                // A game process while the client says "None" is an orphaned
                // replay from an interrupted agent (a live game is InProgress,
                // a watched replay WatchInProgress) - without cleanup it
                // blocks rendering until it happens to exit on its own.
                // Three consecutive polls, so flow transitions never qualify.
                var phaseNow = (string?)null;
                if (LcuClient.TryConnect(LeagueRoot) is { } probe)
                {
                    using (probe) phaseNow = await probe.GetGameflowPhaseAsync(ct);
                }
                // Idle-gated: replays watched via the tracker's links look
                // exactly like orphans (API-launched replays leave gameflow
                // at None - verified live), but a human watching one is not
                // idle. Only an unattended None-phase process qualifies.
                var unattended = GameWindow.UserIdleTime >= TimeSpan.FromSeconds(config.IdleSeconds);
                _orphanStrikes = unattended && phaseNow is "None" ? _orphanStrikes + 1 : 0;
                if (_orphanStrikes >= 3)
                {
                    _orphanStrikes = 0;
                    Log.Warn("Game process running but the client has been out of game for 3 polls - killing the orphaned replay");
                    foreach (var orphan in games)
                    {
                        try { orphan.Kill(entireProcessTree: true); } catch { /* already gone */ }
                        orphan.Dispose();
                    }
                }
                else
                {
                    foreach (var g in games) g.Dispose();
                    Log.Info("Game client running - waiting");
                }
                return false;
            }
            _orphanStrikes = 0;

            // Vanguard only allows replay launches through the League client, so
            // there's no point claiming a job while it's closed. A machine the
            // NAS just woke for queued work has nobody around to open it,
            // though - when work waits and the keyboard is quiet, open it here.
            if (LcuClient.TryConnect(LeagueRoot) is not { } lcu)
            {
                await MaybeLaunchClientAsync(ct);
                return false;
            }
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

    /// The client is closed. Open it (via Riot's launcher - the only
    /// Vanguard-blessed path) when queued work is waiting and nobody is at
    /// the keyboard. With no work it stays closed on purpose: an idle
    /// machine should be free to go back to sleep, not have League opened
    /// on it. Needs "stay signed in" ticked in the Riot client - a launch
    /// parked at a login prompt renders nothing.
    private async Task MaybeLaunchClientAsync(CancellationToken ct)
    {
        if (!config.AutoLaunchClient || _clientLaunchUnavailable)
        {
            Log.Info("League client not running - waiting");
            return;
        }
        // Same politeness bar as rendering itself: a user at the keyboard
        // without League open did not ask for it to appear over their work.
        if (GameWindow.UserIdleTime < TimeSpan.FromSeconds(config.IdleSeconds))
        {
            Log.Info("League client not running - waiting (user is active, not launching over them)");
            return;
        }
        // One attempt per window: a cold start or a patching client looks
        // closed from here for a couple of minutes, and re-asking is noise.
        // Short enough that stage two (the Play press below) follows a cold
        // start that parked at the hub without much of a wait.
        if (DateTime.UtcNow - _lastClientLaunchAttempt < TimeSpan.FromMinutes(3))
        {
            Log.Info("League client not running - a launch is in progress (patching/login can take minutes)");
            return;
        }

        var pending = 0;
        foreach (var tracker in _trackers) pending += await tracker.PendingRenderCountAsync(ct);
        if (pending == 0)
        {
            Log.Info("League client not running - no queued work, leaving it closed");
            return;
        }

        _lastClientLaunchAttempt = DateTime.UtcNow;

        // Work is waiting and the launch chain starts now. After a
        // Wake-on-LAN wake Windows re-sleeps on the UNATTENDED idle timeout
        // (2 minutes by default) - shorter than the client takes to boot to
        // its first claim, so hold the machine awake through that window;
        // the claimed job's own hold takes over from there.
        KeepAwake.HoldFor(TimeSpan.FromMinutes(5));

        // The hub's API is the launch mechanism, full stop -
        // RiotClientServices ignores --launch-product from cold and warm
        // alike (both observed 2026-08-12); the exe only brings the hub up.
        if (await ClientLauncher.PressPlayAsync(ct))
        {
            Log.Info($"{pending} render job(s) waiting - the Riot hub was already open, pressed Play through its API");
            return;
        }

        if (ClientLauncher.Resolve(LeagueRoot) is not { } launcher)
        {
            // Deterministic: a missing Riot launcher will not appear on a
            // retry - say so once, loudly, and stop pretending it might.
            _clientLaunchUnavailable = true;
            Log.Error("Cannot auto-launch the League client: RiotClientServices.exe not found (checked RiotClientInstalls.json and the League root's sibling folder)");
            return;
        }

        Log.Info($"{pending} render job(s) waiting and the client is closed - starting the Riot hub via {launcher}");
        if (!ClientLauncher.Launch(launcher)) return;

        // Press Play in the same breath: leaving it for the next attempt
        // strands a parked hub for minutes (or indefinitely if the user
        // comes back, since launching never happens while they're active).
        for (var waited = 0; waited < 90; waited += 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (await ClientLauncher.PressPlayAsync(ct, quiet: true))
            {
                Log.Info("Riot hub is up - pressed Play through its API");
                return;
            }
        }
        Log.Warn("The Riot hub's API did not answer within 90s of starting it - retrying within 3 minutes");
    }

    private async Task<bool> ProcessJobAsync(TrackerClient tracker, RenderJob job, CancellationToken ct)
    {
        // Renders generate no input; without this the idle-sleep timer would
        // put the machine to sleep mid-job.
        using var awake = KeepAwake.Hold();

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
        // Verified selection names per follow target, valid for one game
        // process (the verification writes against the live process). Fight
        // windows follow other fighters, so a job can hold several targets.
        var verifiedNames = new Dictionary<string, string?>();
        using var replayApi = new ReplayApiClient();

        async Task<string?> CameraNameForAsync(string? name, string? champion)
        {
            var key = $"{name}|{champion}";
            if (verifiedNames.TryGetValue(key, out var cached)) return cached;
            return verifiedNames[key] = await ResolveCameraNameAsync(replayApi, name, champion, ct);
        }

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
            verifiedNames.Clear();
            await CameraNameForAsync(job.MyName, job.MyChampion);
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

            // The fog/camera dropdowns are clicked PER WINDOW, after its seek:
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
                // Between windows is the safe place to honor a deploy's stop
                // request: the postpone releases the job for the next agent,
                // and the finally below kills our replay process - no orphan.
                if (StopRequested) throw new RenderPostponedException("agent stop requested (deploy in progress)");

                // The player queueing up mid-render takes the machine back:
                // finishing the job would leave two game processes fighting
                // over focus and window title just as their game loads. Same
                // postpone path - the replay dies in the finally, the job
                // re-leases for the next quiet stretch.
                if (await lcu.GetGameflowPhaseAsync(ct) is { Length: > 0 } playerPhase
                    and not ("None" or "WatchInProgress" or "TerminatedInError"))
                {
                    throw new RenderPostponedException($"player entered {playerPhase} mid-render");
                }

                // A "fight" window films from another fighter's POV - resolve
                // its own dropdown slot and selection name. A fight target the
                // replay list doesn't know is bad data, not a transient: skip
                // the window, keep the job (the player's own windows already
                // validated their slot above).
                var targetChampion = window.CameraChampion is { Length: > 0 } wc ? wc : myChampion;
                var targetSlotIndex = window.CameraChampion is { Length: > 0 }
                    ? players.FindIndex(p => ReplayApiClient.ChampionMatches(p.Champion, targetChampion))
                    : slot.Index;
                if (targetSlotIndex < 0)
                {
                    Log.Warn($"Window {window.Index} ({window.Label}): camera target \"{targetChampion}\" is not in the replay's player list - skipping this window");
                    skippedWindows.Add(window.Index);
                    continue;
                }
                var windowSlot = (Index: targetSlotIndex, Blue: players[targetSlotIndex].Blue);
                var windowCameraName = await CameraNameForAsync(
                    window.CameraName is { Length: > 0 } wn ? wn : job.MyName, targetChampion);

                var output = Path.Combine(_workDir, $"{job.MatchId}-w{window.Index:00}.mp4");
                var duration = Math.Max(2, window.EndSec - window.StartSec);
                var preRoll = Math.Max(0, window.StartSec - EngagePreRollSec);
                // A fight window's clip must be rolling by the fight moment
                // itself - a dead camera target whose respawn lands later can
                // only film the aftermath (EUW1_7936338594 w16: a 25s respawn
                // wait pushed recording past the whole fight).
                var fightEventSec = window.Kind is "fight"
                    ? window.Events is { Count: > 0 } events ? events[0].TimeSec : window.StartSec
                    : (int?)null;
                var engaged = false;
                var frozen = 0;
                var skippedThis = false;
                for (var attempt = 1; ; attempt++)
                {
                    Log.Info($"Window {window.Index} ({window.Label}, {window.StartSec}-{window.EndSec}s): seeking...");
                    await replayApi.SetPlaybackAsync(preRoll, paused: true, speed: 1, ct);
                    await WaitForSeekAsync(replayApi, preRoll, ct);
                    await replayApi.SetPlaybackAsync(time: null, paused: false, speed: 1, ct);

                    var result = await EngageCameraAsync(replayApi, windowSlot, attempt, windowCameraName, fightEventSec, ct);
                    if (result is EngageResult.TargetDeadPastFight)
                    {
                        // Deterministic per replay timestamp - a retry or a
                        // postpone replays the same respawn timer. Skip the
                        // window so the rest render and the job names the gap.
                        Log.Warn($"Window {window.Index} ({window.Label}): \"{targetChampion}\" is dead until after the fight - no POV to film it from, skipping this window");
                        skippedWindows.Add(window.Index);
                        skippedThis = true;
                        break;
                    }
                    engaged = result is EngageResult.Engaged;
                    if (!engaged)
                    {
                        if (attempt >= 3) break;
                        Log.Warn($"Window {window.Index}: camera did not engage - retrying");
                        continue;
                    }

                    // Engaging ate a variable slice of the pre-roll while
                    // playback ran, so a fixed duration would shift the clip's
                    // end by the same slack - clipping the play's tail when
                    // engage was quick, trailing past it when it was slow.
                    // Anchor the end to the planned EndSec from where playback
                    // actually is; the floor keeps a short clip when engaging
                    // overran the whole window (dead-champion waits), where
                    // late footage beats none.
                    var playhead = await replayApi.GetPlaybackAsync(ct);
                    var captureSec = playhead is { } at
                        ? Math.Max(8, (int)Math.Round(window.EndSec - at.Time))
                        : duration;
                    Log.Info($"Window {window.Index}: recording {captureSec}s...");
                    var started = DateTime.UtcNow;
                    await CaptureAsync(output, captureSec, ct);
                    await replayApi.SetPlaybackAsync(time: null, paused: true, speed: null, ct);

                    // Desktop Duplication can end the stream early (e.g. a display
                    // mode switch) with ffmpeg still exiting 0 - trust the wall
                    // clock, not the exit code, and redo the window.
                    var recorded = (DateTime.UtcNow - started).TotalSeconds;
                    if (recorded < captureSec - 3 && attempt < 3)
                    {
                        Log.Warn($"Window {window.Index}: capture ended after {recorded:0}s of {captureSec}s - retrying");
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
                    await CaptureEngageFailureAsync(job, window.Index, ct);
                    throw new RenderPostponedException("the camera did not engage (user active?)");
                }

                await tracker.UploadAsync(job, window.Index, output, ct);
                File.Delete(output);
                Log.Info($"Window {window.Index}: uploaded");
            }
            if (skippedWindows is { Count: > 0 })
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

    /// For the game recorder, which shares the resolved tools but runs its
    /// own loop. Both are only meaningful after ValidateAsync; the league
    /// root is null in mock mode (no install to resolve).
    public string ResolvedFfmpeg => _ffmpeg;
    public string? ResolvedLeagueRoot => _gameDir is { Length: > 0 } ? LeagueRoot : null;

    /// selectionName only takes a name exactly as the game knows it, and Riot
    /// ID formats vary - so try what the game itself reports for the tracked
    /// player (champion matches first) and keep the first that verifiably
    /// sticks. Falls back to the server-sent name with a warning.
    private static async Task<string?> ResolveCameraNameAsync(ReplayApiClient api, string? targetName, string? targetChampion, CancellationToken ct)
    {
        // The player list can lag the playback API by a few seconds while the
        // game finishes loading - retry before giving up on a verified name.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            foreach (var name in await api.GetCameraCandidatesAsync(targetName, targetChampion, ct))
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
        if (targetName is { Length: > 0 })
        {
            Log.Warn($"Could not verify a selection for \"{targetName}\" - recording with a free camera");
            await api.FollowPlayerAsync(targetName, ct);
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

    // Replay UI geometry lives in ReplayCameraUi - the between-games review
    // drives the same dropdowns, and one HUD has one set of coordinates.
    private const double PanelX = ReplayCameraUi.PanelX;
    private const double CameraBoxY = ReplayCameraUi.CameraBoxY;
    private const double CameraListBottomY = ReplayCameraUi.CameraListBottomY;
    private const double DropdownRowH = ReplayCameraUi.DropdownRowH;
    private const double FogX = ReplayCameraUi.FogX;
    private const double FogBoxY = ReplayCameraUi.FogBoxY;
    private const double FogBlueY = ReplayCameraUi.FogBlueY;
    private const double FogRedY = ReplayCameraUi.FogRedY;

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

    private enum EngageResult { Engaged, Failed, TargetDeadPastFight }

    /// Clicks the filmed champion's side in the fog dropdown, then the
    /// champion in the camera dropdown, and verifies the camera really
    /// tracks - the Replay API has no working equivalent, and the
    /// verification cannot false-positive while the directed camera is
    /// disabled. Runs while the replay is playing (the lock only engages
    /// during playback). fightEventSec, set for fight windows, is the game
    /// second the clip exists to capture: a dead target whose respawn lands
    /// past it cannot film the fight from anywhere but their fountain.
    private async Task<EngageResult> EngageCameraAsync(ReplayApiClient replayApi, (int Index, bool Blue) slot, int attempt, string? cameraName, int? fightEventSec, CancellationToken ct)
    {
        // Park the free camera away from where the world-reload leaves it,
        // BEFORE the clicks (render-API writes reset the camera mode). The
        // park re-asserts the selection too, in case a prior write cleared it.
        var spot = CameraParkSpots[(attempt - 1) % CameraParkSpots.Length];
        var parked = await replayApi.ParkCameraAsync(spot.X, 1911, spot.Z, cameraName, ct);

        // Fog before camera: the camera lock is the only step with a
        // verification, so it goes last - no click lands on the UI after the
        // verified lock, and a fog mis-click (the dropdown has no readback)
        // gets its stray open list closed by the camera clicks instead of
        // sitting open through the recording. The freshly-initialized UI
        // right after a world reload eats the first clicks (previously
        // absorbed by running fog after the ~5s camera verification), so
        // give it a beat before clicking.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        if (!GameWindow.TryClickAt(GameWindowTitle, FogX, FogBoxY))
        {
            Log.Warn("Could not focus the game window for the fog dropdown");
            return EngageResult.Failed;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
        // The dropdown defaults to All (no fog); pick the filmed champion's
        // side. Deterministic click, idempotent when already set.
        GameWindow.TryClickAt(GameWindowTitle, FogX, slot.Blue ? FogBlueY : FogRedY);
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);

        GameWindow.TryClickAt(GameWindowTitle, PanelX, CameraBoxY);
        await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
        var championRowY = CameraListBottomY - (10 - slot.Index) * DropdownRowH + DropdownRowH / 2;
        GameWindow.TryClickAt(GameWindowTitle, PanelX, championRowY);
        // The game hit-tests against the live cursor on the next frame, so
        // moving the cursor in the same instant as the click voids it.
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        GameWindow.TryMoveCursor(GameWindowTitle, 0.5, 0.35);

        var tracks = await CameraTracksAsync(replayApi, parked, ct);

        // A locked camera parks a dead champion's view at their fountain -
        // for a blue-side player that IS the world-reload corner, so the
        // check cannot tell "locked on a corpse" from "never locked" (the
        // EUW1_7921086396 loop: every attempt re-seeked into the same death).
        // Playback keeps running, so wait out a short respawn and re-check:
        // a locked camera follows the champion out of the fountain, an
        // unlocked one stays. Recording then starts a few seconds into the
        // pre-roll cushion, which the 20s window lead absorbs - unless this
        // is a fight window and the respawn lands past the fight itself, in
        // which case there is nothing left worth filming and waiting would
        // record the aftermath (EUW1_7936338594 w16).
        if (!tracks && cameraName is { Length: > 0 }
            && await replayApi.GetPlayerDeathStateAsync(cameraName, ct) is { IsDead: true } death)
        {
            if (fightEventSec is { } eventSec
                && await replayApi.GetPlaybackAsync(ct) is { } playback
                && playback.Time + death.RespawnIn > eventSec)
            {
                Log.Warn($"Camera target is dead at {playback.Time:0}s with {death.RespawnIn:0}s respawn - back after the fight moment ({eventSec}s)");
                return EngageResult.TargetDeadPastFight;
            }
            if (death.RespawnIn <= 25)
            {
                Log.Info($"Tracked player is dead (respawn in {death.RespawnIn:0}s) - waiting to re-verify the camera lock");
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(death.RespawnIn + 5);
                while (DateTime.UtcNow < deadline
                    && await replayApi.GetPlayerDeathStateAsync(cameraName, ct) is { IsDead: true })
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                tracks = await CameraTracksAsync(replayApi, parked, ct);
            }
        }

        if (!tracks)
        {
            // Which failure is it? Camera still at the park = clicks never
            // landed (or lock has no effect); moved but near the park =
            // stationary champion near the reference; empty selection = a
            // write cleared it. The loops this diagnoses are rare enough
            // that the extra API reads don't matter.
            var current = await replayApi.GetCameraPositionAsync(ct);
            var selection = await replayApi.GetSelectionAsync(ct);
            Log.Warn($"Camera check failed: parked=({parked?.X:0},{parked?.Z:0}) now=({current?.X:0},{current?.Z:0}) selection='{selection}'");
            return EngageResult.Failed;
        }

        // Park the cursor away from the panel and screen edges so it neither
        // shows over the HUD in recordings nor edge-scrolls the camera.
        GameWindow.TryMoveCursor(GameWindowTitle, 0.5, 0.35);
        return EngageResult.Engaged;
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
            if (procs is { Length: > 0 })
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

    /// One full-desktop frame at the moment engagement gave up - it shows what
    /// the camera dropdown actually looked like (mode-specific UI, duplicate
    /// champions, focus thieves), which the position numbers can't.
    private async Task CaptureEngageFailureAsync(RenderJob job, int windowIndex, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(_workDir, $"engage-fail-{job.MatchId}-w{windowIndex:00}.png");
            await RunFfmpegAsync($"-y -f lavfi -i ddagrab=framerate=5 -frames:v 1 -vf hwdownload,format=bgra \"{path}\"", ct);
            Log.Warn($"Engage failure frame saved: {path}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not save an engage failure frame: {ex.Message}");
        }
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

    /// Static so the recorder's smoke test can resolve ffmpeg without going
    /// through tracker validation (which waits for a reachable server).
    public static string ResolveFfmpeg(AgentConfig config)
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

    private void EnsureDirectedCameraDisabled() => ReplayCameraUi.EnsureDirectedCameraDisabled(LeagueRoot);

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
