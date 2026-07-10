using LeagueTracker.RenderAgent;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var config = AgentConfig.Load();
Log.Info($"LeagueTracker render agent · server {config.ServerUrl} · agent \"{config.AgentName}\"");

try
{
    var agent = new RenderAgent(config);
    if (!await agent.ValidateAsync(cts.Token)) return 1;

    await agent.RunAsync(cts.Token);
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
