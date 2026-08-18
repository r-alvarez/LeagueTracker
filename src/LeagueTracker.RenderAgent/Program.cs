using LeagueTracker.RenderAgent;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var config = AgentConfig.Load();
Log.Info($"LeagueTracker agent {AgentConfig.Version} ({config.Role}) · server {config.ServerUrl} · agent \"{config.AgentName}\"");

// Side commands talk to a running agent through the sentinels; only the
// agent proper owns them.
// A bare unzip that gets double-clicked is a setup, not a start.
if (args.Contains("--install") || (SetupForm.NeedsSetup(config) && !args.Contains("--youtube-auth") && Environment.GetEnvironmentVariable("LT_SERVER_URL") is not { Length: > 0 }))
{
    return Installer.Install(config);
}
if (args.Contains("--setup")) return Installer.Setup(config);
if (args.Contains("--uninstall")) return Installer.Uninstall();
if (args.Contains("--pause")) { RenderAgent.SetPaused(true); return 0; }
if (args.Contains("--resume")) { RenderAgent.SetPaused(false); return 0; }

// A sentinel left from the previous deploy would stop this agent instantly.
try { File.Delete(RenderAgent.StopSentinelPath); } catch { /* fine - likely absent */ }

try
{
    if (args.Contains("--youtube-auth"))
    {
        // One-time interactive consent for YouTube uploads: opens the
        // browser, stores the refresh token next to the exe, exits.
        return await YouTubeUploader.AuthorizeAsync(config);
    }

    if (Environment.GetEnvironmentVariable("LT_RECORD_TEST") is ("1" or "true" or "seg" or "wgc") and var testMode)
    {
        // Deliberately before tracker validation - the capture pipeline has
        // no server dependency, and the test must run with the NAS down too.
        if (RenderAgent.ResolveFfmpeg(config) is not { Length: > 0 } ff)
        {
            Log.Error("ffmpeg not found - install it or set FfmpegPath");
            return 1;
        }
        if (testMode is "seg") await GameRecorder.SegmentTestAsync(config, ff, cts.Token);
        else if (testMode is "wgc") await GameRecorder.WgcTestAsync(config, ff, cts.Token);
        else await GameRecorder.RecordTestAsync(config, ff, cts.Token);
        return 0;
    }

    // Single instance per install: two agents on one machine fight over the
    // game client and double-record. A second copy (an update's relaunch
    // whose predecessor has not left yet, a double-click on a running
    // install) asks the other to stop and waits - the newest start wins,
    // which is what an update needs and what a double-click means.
    if (Installer.OtherInstance(Environment.ProcessPath ?? "") is { } other)
    {
        Log.Warn($"Another agent from this folder is running (pid {other.Id}) - asking it to stop first");
        AgentStatus.Set("waiting", "Waiting for the previous agent to stop");
        File.WriteAllText(RenderAgent.StopSentinelPath, $"superseded by pid {Environment.ProcessId}");
        // Five minutes covers a recording finalizing or a render postponing.
        // A process still there after that has finished its work and is
        // stuck on the way out (seen once: the tray teardown) - ending it
        // loses nothing, while giving up here leaves the machine with no
        // agent at all, which is the one outcome that must not happen.
        var gone = other.WaitForExit(TimeSpan.FromMinutes(5));
        if (!gone)
        {
            Log.Warn($"The previous agent (pid {other.Id}) did not stop within 5 minutes - ending it");
            try { other.Kill(); gone = other.WaitForExit(TimeSpan.FromSeconds(30)); }
            catch (Exception ex) { Log.Error($"Could not end pid {other.Id}: {ex.Message}"); }
        }
        try { File.Delete(RenderAgent.StopSentinelPath); } catch { /* best-effort */ }
        if (!gone)
        {
            Log.Error($"The previous agent (pid {other.Id}) will not go away - this one exits");
            return 1;
        }
        Log.Info("Previous agent stopped - continuing");
    }

    var agent = new RenderAgent(config);
    var supervisor = new AgentSupervisor(config, agent.Servers);

    // Quit from the tray goes through the stop sentinel like a deploy does:
    // an in-flight render postpones and a recording finalizes before exit.
    // Deleted again on the way out so the next start isn't stillborn.
    using var tray = Environment.GetEnvironmentVariable("LT_NO_TRAY") is "1" ? null
        : new AgentTray(config,
            quit: () => File.WriteAllText(RenderAgent.StopSentinelPath, "quit from tray"),
            checkForUpdates: () => supervisor.CheckForUpdateAsync(cts.Token));

    if (!await agent.ValidateAsync(cts.Token)) return 1;

    // Server-side defaults and secrets (YouTube credentials above all) before
    // any loop validates its configuration.
    await supervisor.ApplyProfileAsync(cts.Token);
    if (RenderAgent.Paused) Log.Info("Starting paused (remove the 'paused' file next to the exe, or Resume from the tray)");

    if (Environment.GetEnvironmentVariable("LT_REVIEW_TEST") is { Length: > 0 } reviewMatch)
    {
        // Drives one match's review now instead of waiting for a game to end -
        // the only way to exercise replay launch, camera lock and hotkeys
        // without playing a game first.
        if (agent.ResolvedLeagueRoot is not { } testRoot)
        {
            Log.Error("Review test needs a resolved League install");
            return 1;
        }
        await new ReplayReview(config, testRoot, agent.Trackers).RunForMatchAsync(reviewMatch, cts.Token);
        return 0;
    }

    // Rendering and live-game recording are independent loops: renders use
    // the PC while nobody plays, the recorder only acts while somebody does.
    // A recorder-only agent (a friend's PC) never touches the replay client;
    // a renderer-only agent (the dedicated box) never records.
    var loops = new List<Task> { supervisor.RunAsync(cts.Token) };
    if (config.RenderReplays) loops.Add(agent.RunAsync(cts.Token));
    else Log.Info("Replay rendering is off (RenderReplays=false) - this agent only records");
    if (config.RecordGames && agent.ResolvedLeagueRoot is { } leagueRoot)
    {
        loops.Add(new GameRecorder(config, agent.ResolvedFfmpeg, leagueRoot, agent.Trackers).RunAsync(cts.Token));
    }
    else if (config.RecordGames)
    {
        Log.Warn("Game recording is on but no League install was resolved (mock mode?) - recorder not started");
    }
    // Third loop, same reasoning: it only reads the client and the trackers,
    // so nothing it does can disturb a recording in flight.
    if (config.PostGameReview && config.RecordGames && agent.ResolvedLeagueRoot is { } reviewRoot)
    {
        loops.Add(new ReplayReview(config, reviewRoot, agent.Trackers).RunAsync(cts.Token));
    }
    else if (config.PostGameReview && config.RecordGames)
    {
        Log.Warn("Post-game review is on but no League install was resolved (mock mode?) - not started");
    }
    await Task.WhenAll(loops);
    return 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    return 0;
}
catch (Exception ex)
{
    // WinExe has nowhere to print - make sure fatal errors reach agent.log.
    Log.Error($"Fatal: {ex}");
    return 1;
}
finally
{
    // A self-update leaves the sentinel for its apply script to clear (it
    // marks "an exit is expected"); every other exit cleans up after itself.
    if (AgentStatus.Current.State is not "updating")
    {
        try { File.Delete(RenderAgent.StopSentinelPath); } catch { /* best-effort */ }
    }
    // Nothing may keep this process alive once Main is done - not a tray
    // pump that stopped answering, not a stray foreground thread. An agent
    // that has said goodbye but lingers blocks its own successor.
    Log.Info("Exiting.");
    Environment.Exit(Environment.ExitCode);
}
