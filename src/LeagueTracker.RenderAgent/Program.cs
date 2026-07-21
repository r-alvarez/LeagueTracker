using LeagueTracker.RenderAgent;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var config = AgentConfig.Load();
Log.Info($"LeagueTracker render agent · server {config.ServerUrl} · agent \"{config.AgentName}\"");

// A sentinel left from the previous deploy would stop this agent instantly.
try { File.Delete(RenderAgent.StopSentinelPath); } catch { /* fine - likely absent */ }

try
{
    if (Environment.GetEnvironmentVariable("LT_RECORD_TEST") is "1" or "true")
    {
        // Deliberately before tracker validation - the capture pipeline has
        // no server dependency, and the test must run with the NAS down too.
        if (RenderAgent.ResolveFfmpeg(config) is not { Length: > 0 } ff)
        {
            Log.Error("ffmpeg not found - install it or set FfmpegPath");
            return 1;
        }
        await GameRecorder.RecordTestAsync(config, ff, cts.Token);
        return 0;
    }

    var agent = new RenderAgent(config);
    if (!await agent.ValidateAsync(cts.Token)) return 1;

    // Rendering and live-game recording are independent loops: renders use
    // the PC while nobody plays, the recorder only acts while somebody does.
    var loops = new List<Task> { agent.RunAsync(cts.Token) };
    if (config.RecordGames && agent.ResolvedLeagueRoot is { } leagueRoot)
    {
        loops.Add(new GameRecorder(config, agent.ResolvedFfmpeg, leagueRoot).RunAsync(cts.Token));
    }
    else if (config.RecordGames)
    {
        Log.Warn("Game recording is on but no League install was resolved (mock mode?) - recorder not started");
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
