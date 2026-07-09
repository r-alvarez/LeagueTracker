using LeagueTracker.RenderAgent;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var config = AgentConfig.Load();
Log.Info($"LeagueTracker render agent · server {config.ServerUrl} · agent \"{config.AgentName}\"");

var agent = new RenderAgent(config);
if (!await agent.ValidateAsync(cts.Token)) return 1;

await agent.RunAsync(cts.Token);
return 0;
