using System.Diagnostics;

namespace LeagueTracker.RenderAgent;

/// The review that used to happen by accident. Recording the game removed the
/// reason to watch it being played, so the finished game plays ITSELF back -
/// in the real client, camera locked to the player, stopping at each moment
/// that decided something - before the next queue rather than never.
///
/// A loop of its own beside the render and recording loops. It reads the LCU,
/// the trackers and the Replay API, and writes no recording state, so nothing
/// here can cost a capture. What it does take is the screen and the replay
/// client, so the render loop stands down for the length of a session
/// (SessionActive) - otherwise a machine that looks idle because someone is
/// watching would have a second replay launched over the top of this one.
public sealed class ReplayReview(AgentConfig config, string leagueRoot, IReadOnlyList<TrackerClient> trackers)
{
    /// Any of these means the player has moved on to the next game. Checked
    /// at every step, not once: the whole feature is "review between games",
    /// and someone who re-queues has answered the question.
    private static readonly string[] QueueingPhases =
        ["Lobby", "Matchmaking", "ReadyCheck", "ChampSelect", "GameStart", "InProgress", "Reconnect"];

    private static int _sessionActive;

    /// True while a review owns the replay client. Read by the render loop.
    public static bool SessionActive => Volatile.Read(ref _sessionActive) != 0;

    private readonly IReadOnlyList<TrackerClient> _trackers = trackers;

    /// Games already reviewed, so a phase that flickers back out of a game
    /// can't reopen the same session twice.
    private readonly HashSet<string> _reviewed = [];

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info("Post-game review on - the replay opens after a game unless the next one is already queued " +
                 $"({config.PostGameReviewDelaySec}s settle, {config.PostGameReviewWaitMin}min import wait, " +
                 $"{(config.PostGameReviewAutoAdvance ? "rolls on by itself" : "waits for a key at each window")}). " +
                 "F9 next · F8 previous · F10 replay this moment · close the replay to stop");

        string? lastMatchId = null;
        var wasInGame = false;

        while (!ct.IsCancellationRequested)
        {
            if (RenderAgent.StopRequested) { Log.Info("stop.requested found - post-game review exiting"); return; }
            try
            {
                var phase = await PhaseAsync(ct);
                if (phase == "InProgress")
                {
                    wasInGame = true;
                    // The match id is only knowable while the session exists -
                    // once the game has ended the client has forgotten it.
                    lastMatchId ??= await MatchIdAsync(ct);
                }
                else if (wasInGame && RenderAgent.Paused)
                {
                    wasInGame = false;
                    lastMatchId = null;
                }
                else if (wasInGame)
                {
                    wasInGame = false;
                    if (lastMatchId is { Length: > 0 } matchId && _reviewed.Add(matchId))
                    {
                        await OfferReviewAsync(matchId, ct);
                    }
                    else if (lastMatchId is not { Length: > 0 })
                    {
                        Log.Warn("Post-game review: the game ended but its match id was never read from the client - skipping");
                    }
                    lastMatchId = null;
                }

                await Task.Delay(TimeSpan.FromSeconds(phase == "InProgress" ? 15 : 5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"Post-game review pass failed: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// Runs the review for one match right now, skipping the did-a-game-just-
    /// end gate: the smoke test for everything that can only be proven with a
    /// real replay on screen (launch, camera lock, seek, pause, hotkeys).
    /// LT_REVIEW_TEST=EUW1_1234567890.
    public async Task RunForMatchAsync(string matchId, CancellationToken ct)
    {
        foreach (var tracker in _trackers)
        {
            if (await tracker.GetReelAsync(matchId, ct) is not { } reel) continue;
            if (reel.Moments.Count == 0)
            {
                Log.Warn($"Review test: {tracker.Name} has {matchId} but the reel is empty " +
                         "(no fights the player was in, no deaths - or analytics not processed)");
                return;
            }
            Log.Info($"Review test: {reel.Moments.Count} moments for {matchId} as {reel.MyChampion}");
            foreach (var (m, i) in reel.Moments.Select((m, i) => (m, i)))
            {
                Log.Info($"  {i + 1,2}. {Clock(m.StartSec)}-{Clock(m.EndSec)} {m.Title} · {m.Detail}");
            }
            await RunSessionAsync(tracker, reel, ct);
            return;
        }
        Log.Error($"Review test: no configured tracker has {matchId}");
    }

    /// Settle, wait for the tracker to have the game, then run the session -
    /// abandoning at any point the player starts queueing. Silence is the
    /// correct outcome of a declined review.
    private async Task OfferReviewAsync(string matchId, CancellationToken ct)
    {
        if (!await StillIdleAsync(TimeSpan.FromSeconds(config.PostGameReviewDelaySec), ct))
        {
            Log.Info($"Post-game review for {matchId} skipped - next game already being queued");
            return;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(config.PostGameReviewWaitMin);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var tracker in _trackers)
            {
                // The reel doubles as the ownership probe: only the tracker
                // whose account played the game can answer it.
                if (await tracker.GetReelAsync(matchId, ct) is not { Moments.Count: > 0 } reel) continue;
                if (await IsQueueingAsync(ct))
                {
                    Log.Info($"Post-game review for {matchId} skipped - next game already being queued");
                    return;
                }
                await RunSessionAsync(tracker, reel, ct);
                return;
            }
            if (!await StillIdleAsync(TimeSpan.FromSeconds(20), ct))
            {
                Log.Info($"Post-game review for {matchId} skipped - next game already being queued");
                return;
            }
        }
        Log.Warn($"Post-game review for {matchId} gave up - no tracker had the game (with a replay) within " +
                 $"{config.PostGameReviewWaitMin} min");
    }

    /// Launch the replay, lock the camera on the player, and walk the moments.
    private async Task RunSessionAsync(TrackerClient tracker, ReviewReel reel, CancellationToken ct)
    {
        // Watching a replay is mouse-quiet for minutes at a stretch - don't
        // let the idle-sleep timer end the review mid-moment.
        using var awake = KeepAwake.Hold();
        Interlocked.Exchange(ref _sessionActive, 1);
        Process? game = null;
        using var replayApi = new ReplayApiClient();
        try
        {
            using var lcu = LcuClient.TryConnect(leagueRoot)
                ?? throw new InvalidOperationException("League client not running");

            // Never adopt a game process this session did not start. One
            // already running is somebody else's - a live game, a render's
            // replay, a replay opened by hand - and the cleanup below would
            // kill it on the way out.
            if (Process.GetProcessesByName("League of Legends") is { Length: > 0 } running)
            {
                foreach (var p in running) p.Dispose();
                Log.Info("Post-game review skipped - a game or replay is already running");
                return;
            }

            var (platform, gameId) = ParseMatchId(reel.MatchId);
            var roflPath = Path.Combine(await lcu.GetReplaysPathAsync(ct), $"{platform}-{gameId}.rofl");
            if (!File.Exists(roflPath) && !await tracker.TryDownloadReplayAsync(reel.MatchId, roflPath, ct))
            {
                Log.Warn($"Post-game review for {reel.MatchId}: no replay archived yet - nothing to play");
                return;
            }

            // Directed camera steers itself; the review wants the player's own
            // camera and nothing else moving it.
            ReplayCameraUi.EnsureDirectedCameraDisabled(leagueRoot);

            Log.Info($"Post-game review: launching the replay for {reel.MatchId} ({reel.Moments.Count} moments)");
            await lcu.ScanAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await lcu.WatchAsync(gameId, ct);
            game = await WaitForGameAsync(ct);
            await WaitForReplayApiAsync(game, replayApi, ct);
            await replayApi.SetPlaybackAsync(time: null, paused: true, speed: 1, ct);

            var camera = await ResolveCameraAsync(replayApi, reel, ct);
            await WalkMomentsAsync(replayApi, reel, camera, game, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An attended feature: the player is sitting right there, so a
            // failure just needs to say what happened and get out of the way.
            Log.Warn($"Post-game review for {reel.MatchId} ended early: {ex.Message}");
        }
        finally
        {
            // Close the replay - but never while the client says a real game
            // is on. If the player queued up and got into champ select or a
            // game during the session, the process being closed here would be
            // THEIR game, not the review's.
            try
            {
                if (game is { HasExited: false })
                {
                    if (await PhaseAsync(CancellationToken.None) is "InProgress" or "Reconnect")
                    {
                        Log.Warn("Post-game review: a live game is running - leaving the game process alone");
                    }
                    else
                    {
                        game.Kill(entireProcessTree: true);
                    }
                }
            }
            catch { /* already gone */ }
            game?.Dispose();
            Interlocked.Exchange(ref _sessionActive, 0);
            Log.Info("Post-game review finished - the machine is free again");
        }
    }

    /// Who the camera belongs to: the dropdown row to click and which side's
    /// fog to show. Resolved once - the identity never changes, only the
    /// game's memory of it does.
    private sealed record CameraTarget(int Slot, bool Blue, string? SelectionName);

    /// Seeking reloads the world, and the reload drops the camera back to
    /// Manual - so the dropdown has to be re-clicked after EVERY seek, not
    /// once per session (found live 2026-08-07: a session that aimed once
    /// played every moment on the free camera). This is the same reason the
    /// clip pipeline engages per window rather than per job.
    private const int EngageLeadSec = 9;

    private async Task<CameraTarget?> ResolveCameraAsync(ReplayApiClient replayApi, ReviewReel reel, CancellationToken ct)
    {
        var players = await replayApi.GetPlayerListAsync(ct);
        var slot = players.FindIndex(p => ReplayApiClient.ChampionMatches(p.Champion, reel.MyChampion));
        if (slot < 0)
        {
            Log.Warn($"Post-game review: '{reel.MyChampion}' is not in the replay's player list - camera left as-is");
            return null;
        }
        var name = (await replayApi.GetCameraCandidatesAsync(reel.MyRiotId, reel.MyChampion, ct)).FirstOrDefault();
        Log.Info($"Post-game review: camera target {reel.MyChampion} (slot {slot}, {(players[slot].Blue ? "blue" : "red")} side)");
        return new CameraTarget(slot, players[slot].Blue, name);
    }

    /// Seek to a little before the moment, then re-aim the camera while the
    /// lead-in plays - the clicks take a few seconds and the champion lock
    /// only engages during playback, so both are done by the time the window
    /// proper starts.
    ///
    /// Unverified on purpose, unlike the render path's: the clip pipeline
    /// verifies because nobody is watching it, but here the player IS
    /// watching, and a camera that didn't take is obvious to them in a second
    /// and fixable from the same dropdown.
    private static async Task SeekAndAimAsync(
        ReplayApiClient replayApi, ReviewMoment moment, CameraTarget? camera, CancellationToken ct)
    {
        var lead = Math.Max(0, moment.StartSec - EngageLeadSec);
        await replayApi.SetPlaybackAsync(time: lead, paused: false, speed: 1, ct);
        await WaitForSeekAsync(replayApi, lead, ct);
        if (camera is null) return;

        // selectionName drives fog and the target frame (abilities, cooldowns)
        // and is also reset by the reload; the follow-cam itself has no API at
        // all and only ever answers to the dropdown.
        if (camera.SelectionName is { Length: > 0 } name) await replayApi.FollowPlayerAsync(name, ct);
        if (!await ReplayCameraUi.SelectAsync(camera.Slot, camera.Blue, ct))
        {
            Log.Warn("Post-game review: could not focus the replay window to set the camera");
        }
    }

    private static async Task WaitForSeekAsync(ReplayApiClient api, int targetSec, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await api.GetPlaybackAsync(ct) is { Seeking: false } playback
                && Math.Abs(playback.Time - targetSec) < 5)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    /// One moment at a time: seek, play its window, and roll straight into the
    /// next one. The hotkeys are the override, not the motor - F8 to go back
    /// to what just happened, F10 to see it again, close the replay to stop. Set
    /// PostGameReviewAutoAdvance false to park at every window's end instead
    /// and require a keypress to move.
    private async Task WalkMomentsAsync(
        ReplayApiClient replayApi, ReviewReel reel, CameraTarget? camera, Process game, CancellationToken ct)
    {
        using var hotkeys = ReviewHotkeys.TryStart();
        if (hotkeys is null) Log.Warn("Post-game review: no hotkeys - moments will advance on their own");

        for (var i = 0; i < reel.Moments.Count && !ct.IsCancellationRequested; )
        {
            if (game.HasExited) { Log.Info("Post-game review: replay closed"); return; }
            if (await IsQueueingAsync(ct)) { Log.Info("Post-game review: next game queued - closing the replay"); return; }

            var moment = reel.Moments[i];
            Log.Info($"Review {i + 1}/{reel.Moments.Count} · {Clock(moment.TimeSec)} · {moment.Title}" +
                     (moment.Detail is { Length: > 0 } ? $" · {moment.Detail}" : ""));

            // Drain BEFORE the seek, not after: with the reel rolling on by
            // itself, a key pressed while the next moment is loading is the
            // player reacting to the one that just ended (usually "wait, go
            // back") - eating it would make the hotkeys feel dead exactly when
            // they matter most.
            hotkeys?.Drain();
            await SeekAndAimAsync(replayApi, moment, camera, ct);

            // Null = the replay went away. Closing its window IS how a review
            // ends early - there is no quit hotkey, because alt+F4 already
            // does exactly that and needs no explaining.
            var command = await PlayWindowAsync(replayApi, hotkeys, moment, game, ct);
            if (command is null)
            {
                Log.Info("Post-game review: the replay closed - ending the session");
                return;
            }
            switch (command)
            {
                case ReviewHotkeys.Command.Previous:
                    i = Math.Max(0, i - 1);
                    break;
                case ReviewHotkeys.Command.Repeat:
                    break;      // same index: seek back to the top of the window
                default:
                    i++;
                    break;
            }
        }
        Log.Info("Post-game review: that was the last moment");
    }

    /// Plays to the end of the window, then parks and waits. Returns the
    /// command that ended the wait (Next when there are no hotkeys, so a
    /// failed hook degrades to an auto-advancing reel rather than a hang), or
    /// null when the replay itself went away.
    private async Task<ReviewHotkeys.Command?> PlayWindowAsync(
        ReplayApiClient replayApi, ReviewHotkeys? hotkeys, ReviewMoment moment, Process game, CancellationToken ct)
    {
        var parked = false;
        // Generous: a seek across a 40-minute replay is not instant, and the
        // window itself is under a minute.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (game.HasExited) return null;
            if (hotkeys?.TryDequeue() is { } pressed) return pressed;

            if (!parked && await replayApi.GetPlaybackAsync(ct) is { Seeking: false } playback
                && playback.Time >= moment.EndSec)
            {
                // The window is over. Rolling on is the default (and the only
                // option without hotkeys to ask with) - the replay keeps
                // playing through the seek, so there is no dead screen.
                if (config.PostGameReviewAutoAdvance || hotkeys is null) return ReviewHotkeys.Command.Next;

                await replayApi.SetPlaybackAsync(time: null, paused: true, speed: 1, ct);
                parked = true;
                Log.Info("   paused - F9 next · F8 previous · F10 again");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        }
        return ReviewHotkeys.Command.Next;
    }

    private static async Task<Process> WaitForGameAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (Process.GetProcessesByName("League of Legends") is { Length: > 0 } procs)
            {
                foreach (var extra in procs[1..]) extra.Dispose();
                return procs[0];
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new TimeoutException("the client did not start the replay within 90s");
    }

    private static async Task WaitForReplayApiAsync(Process game, ReplayApiClient api, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (game.HasExited)
            {
                throw new InvalidOperationException(
                    $"the replay exited during load (code {game.ExitCode}) - wrong patch or corrupt replay");
            }
            if (await api.GetPlaybackAsync(ct) is not null) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("the Replay API did not come up within 3 minutes");
    }

    /// Waits out the given span, returning false as soon as the player starts
    /// queueing. Polls rather than sleeping through it so a 30s settle can't
    /// swallow a re-queue that happened at second one.
    private async Task<bool> StillIdleAsync(TimeSpan span, CancellationToken ct)
    {
        var until = DateTime.UtcNow + span;
        while (DateTime.UtcNow < until)
        {
            if (await IsQueueingAsync(ct)) return false;
            var remaining = until - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromSeconds(3) ? remaining : TimeSpan.FromSeconds(3), ct);
        }
        return !await IsQueueingAsync(ct);
    }

    /// A client that has gone away (closed after the last game) reads as not
    /// queueing - which it isn't, and the review is still wanted.
    private async Task<bool> IsQueueingAsync(CancellationToken ct) =>
        await PhaseAsync(ct) is { } phase && QueueingPhases.Contains(phase);

    private async Task<string?> PhaseAsync(CancellationToken ct)
    {
        if (LcuClient.TryConnect(leagueRoot) is not { } lcu) return null;
        using (lcu) return await lcu.GetGameflowPhaseAsync(ct);
    }

    private async Task<string?> MatchIdAsync(CancellationToken ct)
    {
        if (LcuClient.TryConnect(leagueRoot) is not { } lcu) return null;
        using (lcu)
        {
            return await lcu.GetGameSessionAsync(ct) is { PlatformId: { Length: > 0 } platform } session
                ? $"{platform}_{session.GameId}"
                : null;
        }
    }

    private static (string Platform, long GameId) ParseMatchId(string matchId)
    {
        var parts = matchId.Split('_');
        return parts.Length == 2 && long.TryParse(parts[1], out var id)
            ? (parts[0], id)
            : throw new FormatException($"unrecognised match id '{matchId}'");
    }

    private static string Clock(int sec) => $"{sec / 60}:{sec % 60:00}";
}
