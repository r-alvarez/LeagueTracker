using System.Text;
using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Auth;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Match = LeagueTracker.Api.Data.Match;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "LeagueTracker");
builder.Services.Configure<RiotOptions>(builder.Configuration.GetSection("Riot"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.Configure<AccountsOptions>(builder.Configuration.GetSection("Accounts"));
builder.Services.Configure<AgentsOptions>(builder.Configuration.GetSection("Agents"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

// The global registry: users, the accounts they own, the machines they
// enrolled. One SQLite next to the account folders; the per-account
// databases stay what they were.
builder.Services.AddSingleton<RegistryDatabase>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<RegistryBootstrap>();
builder.Services.AddSingleton<ClaimService>();

// One process, many tracked accounts: each request/job is bound to one
// (AccountContext) and everything account-shaped - data folder, SQLite,
// Riot routing, live game, running job, render leases - resolves through it.
builder.Services.AddSingleton<AccountRegistry>();
builder.Services.AddScoped<AccountContext>();
builder.Services.AddSingleton<AccountScopes>();
builder.Services.AddSingleton<AccountInitializer>();
builder.Services.AddDbContext<LeagueDbContext>((sp, o) =>
    o.UseSqlite($"Data Source={Path.Combine(sp.GetRequiredService<AccountContext>().DataDir, "leaguetracker.db")}"));

builder.Services.AddSingleton<RiotRateLimiter>();
builder.Services.AddSingleton<IRiotKeyProvider, RiotKeyProvider>();
builder.Services.AddSingleton<RankCache>();
builder.Services.AddPerAccount<JobStatusService>();
builder.Services.AddTransient<RiotRateLimitHandler>();
// Generous timeout: the rate limiter paces requests INSIDE the handler, so a
// burst legitimately waits out the key's 2-minute budget before sending.
builder.Services.AddHttpClient<RiotApiClient>(c => c.Timeout = TimeSpan.FromMinutes(10))
    .AddHttpMessageHandler<RiotRateLimitHandler>();

builder.Services.AddScoped<DataPaths>();
builder.Services.AddScoped<RankLookupService>();
builder.Services.AddScoped<LpService>();
builder.Services.AddScoped<MatchIngestService>();
builder.Services.AddScoped<TrackedPlayerService>();
builder.Services.AddScoped<HistorySyncService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<AnalyticsReprocessService>();
builder.Services.AddScoped<ChallengesBenchmarkService>();
builder.Services.AddScoped<ReplayArchiveService>();
builder.Services.AddScoped<ClipService>();
builder.Services.AddScoped<FullGameService>();
builder.Services.AddScoped<TimelineSeriesService>();
builder.Services.AddScoped<LensService>();
builder.Services.AddScoped<FundamentalsService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<ReviewReelService>();
builder.Services.AddPerAccount<RenderLeaseService>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<AgentKeyStore>();
builder.Services.AddHttpClient("github", c =>
{
    c.Timeout = TimeSpan.FromMinutes(10);   // a 130 MB asset on a slow day
    c.DefaultRequestHeaders.UserAgent.ParseAdd("LeagueTracker/1.0 (+https://github.com/r-alvarez/LeagueTracker)");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddHostedService<AgentReleaseSyncService>();
builder.Services.AddScoped<VodService>();
builder.Services.AddPerAccount<LiveGameState>();
builder.Services.AddHostedService<MatchPollerService>();

// Vite dev server origin - Development only: with a session cookie in play a
// standing cross-origin allowance would be a CSRF/exfiltration path.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
}

builder.AddTrackerAuth();

// Tracking a Riot ID costs a Riot call, a folder, a database and a permanent
// poller slot: a signed-in person gets a handful per hour, not a script's worth.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("account-add", http => System.Threading.RateLimiting.RateLimitPartition.GetSlidingWindowLimiter(
        http.User.FindFirst(TrackerClaims.UserId)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new System.Threading.RateLimiting.SlidingWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1), SegmentsPerWindow = 4, QueueLimit = 0 }));
});

// Behind Traefik (and Cloudflare in front of it) the socket peer is always the
// proxy: without this every agent enrolment shared one "IP", the per-IP
// pending cap locked everyone out after three, and the Data page's LastIp -
// the owner's one clue that a pending machine is really a friend's - was the
// proxy's address. Nothing reaches the container except through the proxy
// (no host ports on the NAS), so every hop is trusted; Cloudflare's own
// header carries the client past the edge.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Compress JSON app-side: the Traefik deployment doesn't compress (the old
// Caddy proxy did), and this way every proxy setup gets it for free.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

var app = builder.Build();

// Every account gets its db created/upgraded now; one that fails is marked
// unavailable (503 on its API, skipped by the poller, retried every minute)
// instead of taking the process down.
var initializer = app.Services.GetRequiredService<AccountInitializer>();
foreach (var account in app.Services.GetRequiredService<AccountRegistry>().All) initializer.EnsureReady(account);
app.Services.GetRequiredService<RegistryBootstrap>().Run();
// Built now, not on the first agent request: its one-time agents.json import
// belongs in the boot log next to the account import.
app.Services.GetRequiredService<AgentKeyStore>();

app.UseForwardedHeaders();
app.Use((http, next) =>
{
    if (http.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf)
        && System.Net.IPAddress.TryParse(cf.FirstOrDefault(), out var client))
    {
        http.Connection.RemoteIpAddress = client;
    }
    return next(http);
});
app.UseRouting();
if (app.Environment.IsDevelopment()) app.UseCors();
// Binding needs the route values (after routing) and must precede
// authorization (the Owner/Agent policies judge against the bound account).
app.UseMiddleware<AccountBindingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseCsrfGuard();
app.UseResponseCompression();
// Hashed bundles never change; everything else (index.html above all) must
// revalidate every load - without an explicit Cache-Control, browsers apply
// heuristic freshness and keep serving a pre-deploy bundle for days.
var staticFiles = new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/assets")
            ? "public,max-age=31536000,immutable"
            : "no-cache",
};
app.UseDefaultFiles();
app.UseStaticFiles(staticFiles);

// One mount for humans and machines: /api/a/{region}/{slug}/... - the SPA
// names the account, an agent names it on its key. The binding middleware
// has chosen the account by the time a handler runs; a slug from before a
// rename redirects there.
MapAccountApi(app.MapGroup("/api/a/{region:regex(^[a-z]{{2,4}}$)}/{slug}"));

// An account whose db could not be opened answers 503 with the reason on
// every account-scoped route (this also retries the initialisation once a
// minute, so a transient lock clears itself); the global routes - the
// account list, agent enrolment, releases - keep working regardless.
static async ValueTask<object?> RequireAvailableAccount(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var account = context.HttpContext.RequestServices.GetRequiredService<AccountContext>().Current;
    var initializer = context.HttpContext.RequestServices.GetRequiredService<AccountInitializer>();
    return initializer.EnsureReady(account)
        ? await next(context)
        : Results.Problem($"{account.RiotId} is unavailable: {initializer.ErrorFor(account)}", statusCode: 503, title: "Account unavailable");
}

object AccountView(Account a) => new
{
    a.Id, a.Slug, a.Label, a.RiotId, a.GameName, a.TagLine, a.HideLp, a.Platform,
    Region = a.RegionCode, Path = a.UrlPath, a.FromConfig,
    Owned = a.IsOwned, a.OwnerUserId, a.MediaPublic,
    PreviousSlugs = a.PreviousSlugList,
    Available = initializer.IsReady(a),
    Unavailable = initializer.ErrorFor(a),
};

app.MapGet("/api/accounts", (AccountRegistry registry, AccountContext acct) => Results.Ok(new
{
    Default = registry.Default.Slug,
    // The account this request is bound to (Host header on a legacy
    // hostname, else the default) - the SPA lands there when the URL has no slug.
    Current = acct.Slug,
    registry.CanAdd,
    Regions = Platforms.All.Select(p => new { Code = p.Code, p.Label, p.Platform }),
    Accounts = registry.All.Select(AccountView),
}));

// The "add account" box: a Riot ID typed by a person, checked against Riot
// (account-v1 answers with the canonical casing and the puuid), then given a
// folder, a database and a place in the poller's round - no redeploy.
app.MapPost("/api/accounts", async (AddAccountRequest request, AccountRegistry registry, AccountScopes scopes, AccountInitializer initializer, IRiotKeyProvider keys, CancellationToken ct) =>
{
    if (!registry.CanAdd) return Results.Problem("This deployment takes accounts from configuration only (Accounts:DataRoot is not set)", statusCode: 409);
    var (gameName, tagLine) = ParseRiotId(request.RiotId);
    if (gameName is null || tagLine is null) return Results.BadRequest(new { error = "Type the Riot ID as GameName#TAG" });
    var platform = Platforms.ByCode(request.Region) ?? Platforms.ByPlatform(request.Region);
    if (platform is null) return Results.BadRequest(new { error = $"Unknown region '{request.Region}'" });
    if (registry.BySlug($"{gameName}-{tagLine}") is { } existing) return Results.Conflict(new { error = $"{existing.RiotId} is already tracked", account = AccountView(existing) });
    if (keys.GetKey() is null) return Results.Problem("No Riot API key configured - the account cannot be verified", statusCode: 503);

    // Resolve through a scope bound to a throwaway account with the target
    // routing: account-v1 is regional, and the typed casing may be off.
    var probe = new Account { GameName = gameName, TagLine = tagLine, Platform = platform.Platform, Region = platform.Region, DataDir = Path.GetTempPath() };
    AccountDto resolved;
    using (var scope = scopes.Create(probe))
    {
        try
        {
            resolved = await scope.ServiceProvider.GetRequiredService<RiotApiClient>().GetAccountAsync(gameName, tagLine, ct);
        }
        catch (RiotApiException ex) when (ex.StatusCode == 404)
        {
            return Results.NotFound(new { error = $"Riot knows no account {gameName}#{tagLine} in {platform.Label}" });
        }
        catch (RiotApiException ex)
        {
            return Results.Problem($"Riot answered {ex.StatusCode} while checking the account{(ex.IsAuthFailure ? " - the API key is invalid or expired" : "")}", statusCode: 502);
        }
    }
    if (registry.ByPuuid(resolved.Puuid) is { } samePlayer) return Results.Conflict(new { error = $"{samePlayer.RiotId} is already tracked (same player, renamed)", account = AccountView(samePlayer) });
    var account = registry.Add(resolved.GameName ?? gameName, resolved.TagLine ?? tagLine, platform.Platform, request.DisplayName, resolved.Puuid);
    if (!initializer.EnsureReady(account))
    {
        return Results.Problem($"{account.RiotId} is registered but its database could not be initialised: {initializer.ErrorFor(account)}", statusCode: 503);
    }
    using (var scope = scopes.Create(account))
    {
        await scope.ServiceProvider.GetRequiredService<TrackedPlayerService>().StorePuuidAsync(resolved.Puuid, ct);
    }
    return Results.Created($"/{account.UrlPath}/", AccountView(account));
}).RequireAuthorization(Policies.User).RequireRateLimiting("account-add");

// Untrack (the folder stays on the NAS). Configured accounts say no; so
// does anyone but the owner or an admin.
app.MapDelete("/api/accounts/{idOrSlug}", (string idOrSlug, Caller caller, AccountRegistry registry, AccountInitializer initializer) =>
{
    var decoded = Uri.UnescapeDataString(idOrSlug);
    if ((registry.ById(decoded) ?? registry.BySlug(decoded)) is not { } account) return Results.NotFound();
    if (!caller.Owns(account)) return Results.Forbid();
    if (!registry.Remove(account.Id)) return Results.Conflict(new { error = "configured accounts are removed from the compose, not here" });
    initializer.Forget(account.Id);
    return Results.NoContent();
}).RequireAuthorization(Policies.User);

static (string? GameName, string? TagLine) ParseRiotId(string? riotId)
{
    if (riotId is not { Length: > 0 }) return (null, null);
    var hash = riotId.LastIndexOf('#');
    if (hash <= 0 || hash == riotId.Length - 1) return (null, null);
    var name = riotId[..hash].Trim();
    var tag = riotId[(hash + 1)..].Trim();
    return name.Length is >= 3 and <= 16 && tag.Length is >= 3 and <= 5 ? (name, tag) : (null, null);
}


// --- Agents ---------------------------------------------------------------------
// The agents on the players' PCs are the moving parts nobody watches: they
// announce themselves every poll, take their defaults + secrets from here (so
// a friend's install is ServerUrl + Access token and nothing else), and pull
// new builds from ReleaseDir - one publish on the NAS updates every machine.

// --- Agent enrolment ------------------------------------------------------------
// A new machine knocks with only the URL: it generates a key, enrols, and
// waits; the owner approves on the Data page. AgentAuthMiddleware lets
// enroll/ping/release through anonymously and demands an approved key for
// the rest of /api/agent/*. Human management lives under /api/agents
// (behind Access like every other human route).

app.MapAuthEndpoints();

app.MapPost("/api/agent/enroll", (EnrollRequest request, HttpContext http, AgentKeyStore keys) =>
{
    if (request.Key is not { Length: >= 32 } || request.Key.Length > 200) return Results.BadRequest(new { error = "key must be 32-200 characters" });
    var (record, created, refusal) = keys.Enroll(request.Key, request.Name ?? "", request.Machine ?? "unknown", http.Connection.RemoteIpAddress?.ToString(), request.Code);
    if (refusal is not null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    return Results.Ok(new { record.Id, Status = record.Status.ToString().ToLowerInvariant(), Created = created });
});

app.MapGet("/api/agent/enroll/status", (HttpContext http, AgentKeyStore keys) =>
    http.Request.Headers[AgentKeyAuthenticationHandler.HeaderName].FirstOrDefault() is { Length: >= 16 } key && keys.Find(key) is { } record
        ? Results.Ok(new { record.Id, Status = record.Status.ToString().ToLowerInvariant() })
        : Results.NotFound(new { error = "unknown key - enrol first" }));

app.MapGet("/api/agent/ping", (Caller caller) =>
    Results.Ok(new { ok = true, server = "leaguetracker", authenticated = caller.IsAgent }));

// The accounts this machine may act on: a recorder gets its owner's (with
// puuids, so "who was playing" is matched on the identity that never
// renames), a renderer gets everyone's. An unbound key gets everyone's only
// while the rollout flag says so.
app.MapGet("/api/agent/accounts", (AccountRegistry registry, Caller caller, AgentKeyStore keys) =>
{
    var agent = caller.Agent!;
    var reachable = agent.Role is AgentRole.Renderer || (!agent.IsBound && keys.AllowUnbound)
        ? registry.All
        : registry.OwnedBy(agent.OwnerUserId ?? "");
    return Results.Ok(new
    {
        Default = reachable.FirstOrDefault()?.Slug ?? registry.Default.Slug,
        Role = agent.Role.ToString().ToLowerInvariant(),
        Bound = agent.IsBound,
        Accounts = reachable.Select(a => new { a.Id, a.Slug, a.Label, a.RiotId, a.GameName, a.TagLine, a.Puuid, a.Platform, Region = a.RegionCode, Path = a.UrlPath, Available = initializer.IsReady(a) }),
    });
}).RequireAuthorization(Policies.Agent);

// Human management of machines lives under /api/me (own) and /api/admin
// (all) - see ManagementEndpoints. Nothing under /api/agents any more, so
// no path prefix of the agent slice ever names a human route again.
app.MapManagementEndpoints();

// The key that authenticated names the agent, which selects the per-agent
// overrides (Agent__Profiles__<name>__...).
app.MapGet("/api/agent/profile", (Caller caller, AgentRegistry agents) => Results.Ok(agents.ProfileFor(caller.Agent!.Name))).RequireAuthorization(Policies.Agent);

// The agent's side of "sendlog": the tail of agent.log, filed under the key
// that authenticated - only an approved agent writes here, only as itself.
app.MapPost("/api/agent/log", async (HttpContext http, Caller caller, AgentRegistry agents, CancellationToken ct) =>
{
    using var reader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8);
    var text = await reader.ReadToEndAsync(ct);
    if (text.Length > 2_000_000) text = text[^2_000_000..];
    var file = agents.StoreLog(caller.Agent!.Id, text);
    return Results.Ok(new { file });
}).RequireAuthorization(Policies.Agent);

// The heartbeat's identity is the key that authenticated it; the name in
// the body is display text at most.
app.MapPost("/api/agent/heartbeat", (AgentHeartbeat beat, Caller caller, AgentRegistry agents) =>
{
    var key = caller.Agent!;
    agents.Record(key, beat);
    var pending = agents.PendingCommand(key.Id);
    return Results.Ok(new { latest = agents.Latest()?.Version, command = pending?.Command, commandToken = pending?.Token });
}).RequireAuthorization(Policies.Agent);

app.MapGet("/api/agent/agents", (AgentRegistry agents) => Results.Ok(agents.Snapshot())).RequireAuthorization(Policies.Agent);

// The waker on the NAS needs one bit - is render work waiting anywhere -
// and it has no identity; counts only, across every account.
app.MapGet("/api/render/pending", async (AccountRegistry registry, AccountScopes scopes, AccountInitializer initializer, CancellationToken ct) =>
{
    var pending = 0;
    foreach (var account in registry.All.Where(initializer.IsReady))
    {
        using var scope = scopes.Create(account);
        var leases = scope.ServiceProvider.GetRequiredService<RenderLeaseService>();
        var rows = (await scope.ServiceProvider.GetRequiredService<ClipService>().QueueAsync(leases, ct))
            .Concat(await scope.ServiceProvider.GetRequiredService<FullGameService>().QueueRowsAsync(leases, ct));
        // The queue rows are the anonymous shapes the Data page renders; the
        // status is the one field this needs.
        pending += rows.Count(r => System.Text.Json.JsonSerializer.SerializeToElement(r).GetProperty("Status").GetString() is "pending" or "partial");
    }
    return Results.Ok(new { pending });
});

app.MapGet("/api/agent/release", (AgentRegistry agents) =>
    agents.Latest() is { } release ? Results.Ok(release) : Results.NoContent());

app.MapGet("/api/agent/release/{file}", (string file, AgentRegistry agents) =>
    agents.ReleasePath(file) is { } path
        ? Results.File(path, file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "application/vnd.microsoft.portable-executable" : "application/zip", file, enableRangeProcessing: true)
        : Results.NotFound());

app.MapFallbackToFile("index.html", staticFiles);

app.Run();

void MapAccountApi(RouteGroupBuilder api)
{
api.AddEndpointFilter(RequireAvailableAccount);
// Every endpoint sits in the group of the policy it needs; the groups share
// the account prefix and the availability filter, and differ only in who
// may call. Read = Riot-derived data (public once Auth:PublicReads is on),
// Owner = the account's owner or an admin, Media = recordings/clips/renders
// (owner-only unless the owner shares them), the agent groups = machines
// with an approved key bound to the right owner or role.
var read = api.MapGroup("").RequireAuthorization(Policies.Read);
var owner = api.MapGroup("").RequireAuthorization(Policies.Owner);
var media = api.MapGroup("").RequireAuthorization(Policies.MediaRead);
var recorder = api.MapGroup("").RequireAuthorization(Policies.AgentRecorder);
var render = api.MapGroup("").RequireAuthorization(Policies.AgentRender);
var renderRead = api.MapGroup("").RequireAuthorization(Policies.RenderRead);

// --- Status ---------------------------------------------------------------------

read.MapGet("/status", async (AccountContext acct, Caller caller, LeagueDbContext db, LpService lp, TrackedPlayerService player, IRiotKeyProvider keys, JobStatusService jobs, ReplayArchiveService replays, AgentRegistry agents, CancellationToken ct) =>
{
    var solo = await lp.GetLatestAsync("Solo/Duo", ct);
    var flex = await lp.GetLatestAsync("Flex", ct);
    var hasMatches = await db.Matches.AnyAsync(ct);
    // Operational state (key, running job, this owner's machines) is the
    // owner's business; visitors get the public figures only.
    var manages = caller.Owns(acct.Current) && caller.IsUser;
    return Results.Ok(new
    {
        player.RiotId,
        Account = new { acct.Current.Id, Owned = acct.Current.IsOwned, acct.Current.MediaPublic },
        ApiKeyConfigured = manages ? keys.GetKey() is not null : (bool?)null,
        Matches = await db.Matches.CountAsync(ct),
        RankedMatches = await db.Matches.CountAsync(m => m.IsRanked, ct),
        Deaths = await db.Deaths.CountAsync(ct),
        LpSnapshots = await db.LpSnapshots.CountAsync(ct),
        Replays = replays.ArchivedMatchIds().Count,
        Patches = await Reports.PatchesAsync(db, ct),
        DateFrom = hasMatches ? (await db.Matches.MinAsync(m => m.GameCreationUtc, ct)).ToLocalTime().ToString("yyyy-MM-dd") : null,
        DateTo = hasMatches ? (await db.Matches.MaxAsync(m => m.GameCreationUtc, ct)).ToLocalTime().ToString("yyyy-MM-dd") : null,
        HideLp = acct.Current.HideLp,
        Ranks = new[] { solo, flex }.Where(s => s is not null && !acct.Current.HideLp).Select(s => new
        {
            s!.Queue, s.Tier, s.Division, s.Lp, s.Wins, s.Losses, s.RankValue,
            Label = $"{s.Tier} {s.Division} {s.Lp} LP",
            AsOfUtc = s.TimestampUtc,
        }),
        Job = manages ? jobs.Snapshot() : null,
        Agents = manages ? agents.SnapshotFor(acct.Current.OwnerUserId, caller.IsAdmin) : [],
    });
});

// The owner's dials for the profile: whether recordings show to visitors,
// whether LP/rank stays private, the display name in the switcher.
owner.MapPut("/settings", (AccountSettingsRequest request, AccountContext acct) =>
{
    acct.Registry.Update(acct.Current, a =>
    {
        if (request.MediaPublic is { } media) a.MediaPublic = media;
        if (request.HideLp is { } hide) a.HideLp = hide;
        if (request.DisplayName is { } name) a.DisplayName = name.Trim()[..Math.Min(name.Trim().Length, 40)];
    });
    return Results.Ok(new { acct.Current.Id, acct.Current.MediaPublic, acct.Current.HideLp, acct.Current.DisplayName });
});

// The game being played right now (spectator-v5, refreshed by the poller).
read.MapGet("/live", (AccountContext acct, LiveGameState live) =>
    live.Current is { } g
        ? Results.Ok(new
        {
            g.MatchId, g.QueueId, Queue = RankMath.QueueName(g.QueueId),
            g.StartedUtc, g.ClockStartUtc, g.DetectedUtc, g.MyChampionId, g.MyTeamId,
            AvgAllyRank = !acct.Current.HideLp && g.AvgAllyRankValue is { } ally ? RankMath.ToLabel(ally) : null,
            AvgEnemyRank = !acct.Current.HideLp && g.AvgEnemyRankValue is { } enemy ? RankMath.ToLabel(enemy) : null,
            RankGapLp = !acct.Current.HideLp && g is { AvgAllyRankValue: { } a, AvgEnemyRankValue: { } e } ? (int?)Math.Round(e - a) : null,
            Participants = g.Participants.Select(p => new { p.ChampionId, p.TeamId, p.RiotId, p.IsMe }),
        })
        : Results.NoContent());

// --- Matches --------------------------------------------------------------------

read.MapGet("/matches", async (AccountContext acct, LeagueDbContext db, ReplayArchiveService replays, int page = 1, int pageSize = 20, bool? ranked = null,
    string? champion = null, string? opponent = null, string? role = null, string? queue = null, string? patch = null, CancellationToken ct = default) =>
{
    var query = db.Matches.AsNoTracking();
    if (ranked is not null) query = query.Where(m => m.IsRanked == ranked);
    if (champion is { Length: > 0 }) query = query.Where(m => m.Champion == champion);
    if (opponent is { Length: > 0 }) query = query.Where(m => m.OpponentChampion == opponent);
    if (role is { Length: > 0 })
    {
        var normalized = role.ToUpperInvariant();
        query = query.Where(m => m.Position == normalized);
    }
    if (queue is { Length: > 0 })
    {
        // An unknown family is a 400, not "everything" - silently returning all
        // matches reads as "this player has 400 Arena games".
        if (RankMath.QueueFamily(queue) is not { } queueIds) return Results.BadRequest(new { error = $"Unknown queue '{queue}'" });
        query = query.Where(m => queueIds.Contains(m.QueueId));
    }
    // Patches are major.minor; GameVersion carries the full build number.
    if (patch is { Length: > 0 }) query = query.Where(m => m.GameVersion.StartsWith(patch + "."));

    var total = await query.CountAsync(ct);
    var items = await query
        .OrderByDescending(m => m.GameEndUtc)
        .Skip((Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 200)).Take(Math.Clamp(pageSize, 1, 200))
        .ToListAsync(ct);

    // Per-row participant context: my loadout, plus the role companions for the
    // matchup block (mid/top pair with junglers, bot with supports,
    // support with bot carries, junglers with mid).
    var ids = items.Select(m => m.Id).ToList();
    var participants = (await db.Participants.AsNoTracking()
            .Where(p => ids.Contains(p.MatchId))
            .Select(p => new { p.MatchId, p.Champion, p.Position, p.IsAlly, p.IsMe, p.Items, p.Summoner1Id, p.Summoner2Id })
            .ToListAsync(ct))
        .GroupBy(p => p.MatchId)
        .ToDictionary(g => g.Key, g => g.ToList());

    static string CompanionRole(string myPosition) => myPosition switch
    {
        "BOTTOM" => "UTILITY",
        "UTILITY" => "BOTTOM",
        "JUNGLE" => "MIDDLE",
        _ => "JUNGLE",
    };

    var archived = replays.ArchivedMatchIds();
    return Results.Ok(new
    {
        total,
        items = items.Select(m =>
        {
            var ps = participants.GetValueOrDefault(m.Id);
            var mine = ps?.FirstOrDefault(p => p.IsMe);
            var role = CompanionRole(m.Position);
            return MatchListItem(m, mine?.Items, mine?.Summoner1Id, mine?.Summoner2Id,
                archived.Contains(m.Id),
                myCompanion: ps?.FirstOrDefault(p => p.IsAlly && !p.IsMe && p.Position == role)?.Champion,
                enemyCompanion: ps?.FirstOrDefault(p => !p.IsAlly && p.Position == role)?.Champion,
                companionRole: role,
                hideLp: acct.Current.HideLp);
        }),
    });
});

// Filter options for the match list: every champion/opponent with game counts,
// plus the patches - so the pickers only ever offer values that exist.
read.MapGet("/matches/facets", async (LeagueDbContext db, CancellationToken ct) =>
{
    var champions = await db.Matches.AsNoTracking()
        .GroupBy(m => m.Champion)
        .Select(g => new { Name = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count).ThenBy(x => x.Name)
        .ToListAsync(ct);
    var opponents = await db.Matches.AsNoTracking()
        .Where(m => m.OpponentChampion != null && m.OpponentChampion != "")
        .GroupBy(m => m.OpponentChampion!)
        .Select(g => new { Name = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count).ThenBy(x => x.Name)
        .ToListAsync(ct);
    return Results.Ok(new
    {
        Champions = champions,
        Opponents = opponents,
        Patches = await Reports.PatchesAsync(db, ct),
    });
});

// The archived official .rofl for a game (playable in the client on the same patch).
renderRead.MapGet("/matches/{id}/replay", (string id, ReplayArchiveService replays) =>
    replays.PathFor(id) is { } path
        ? Results.File(path, "application/octet-stream", $"{id}.rofl")
        : Results.NotFound());

// The planned highlight windows for a game and whether each mp4 has landed yet.
media.MapGet("/matches/{id}/clips", async (AccountContext acct, string id, ClipService clips, CancellationToken ct) =>
{
    var plan = await clips.LoadPlanAsync(id, ct);
    return Results.Ok(plan is null
        ? []
        : plan.Windows.Select(w => new
        {
            w.Index, w.Label, w.StartSec, w.EndSec, w.Events, w.Kind, w.CameraChampion,
            Url = $"/api/a/{acct.UrlSegment}/matches/{id}/clips/{w.Index}",
            Ready = clips.ClipPath(id, w.Index) is not null,
        }));
});

// Range processing on: the <video> scrub bar needs partial requests.
media.MapGet("/matches/{id}/clips/{index:int}", (string id, int index, ClipService clips) =>
    clips.ClipPath(id, index) is { } path
        ? Results.File(path, "video/mp4", enableRangeProcessing: true)
        : Results.NotFound());

// Drop one bad clip (e.g. the render silently captured a hung replay); the
// window re-enters the render queue and the agent re-creates just that mp4.
owner.MapDelete("/matches/{id}/clips/{index:int}", (string id, int index, ClipService clips) =>
    clips.DeleteClip(id, index) ? Results.Ok() : Results.NotFound());

// --- Live-game VODs (recorded by the agent while the player was in game) ---------

media.MapGet("/matches/{id}/vod/status", (string id, VodService vods) =>
    Results.Ok(vods.Status(id)));

media.MapGet("/matches/{id}/vod", (string id, VodService vods) =>
    vods.VideoPath(id) is { } path
        ? Results.File(path, "video/mp4", enableRangeProcessing: true)
        : Results.NotFound());

media.MapGet("/matches/{id}/vod/thumb", (string id, VodService vods) =>
    vods.ThumbPath(id) is { } path
        ? Results.File(path, "image/jpeg")
        : Results.NotFound());

owner.MapDelete("/matches/{id}/vod", (string id, VodService vods) =>
{
    vods.Delete(id);
    return Results.Ok();
});

// The match's YouTube upload (storage-free review mode: the video lives on
// YouTube, the tracker keeps only markers/APM data). Empty url = unlink.
recorder.MapPost("/matches/{id}/vod/link", async (string id, HttpRequest request, VodService vods, LeagueDbContext db, CancellationToken ct) =>
{
    if (!await db.Matches.AsNoTracking().AnyAsync(m => m.Id == id, ct)) return Results.NotFound();
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString()?.Trim() : null;
    if (url is { Length: > 0 } && !System.Text.RegularExpressions.Regex.IsMatch(
            url, @"^https://(www\.)?(youtube\.com/(watch\?|shorts/)|youtu\.be/)"))
    {
        return Results.BadRequest(new { error = "that does not look like a YouTube video link" });
    }
    vods.SaveLink(id, url);
    return Results.Ok(vods.Status(id));
});

// Agent-facing uploads. The mp4 is only accepted for a match this tracker
// knows - the one agent serves several trackers and offers each VOD to all
// of them; the owning tracker is the one whose db has the match.
recorder.MapPut("/vods/{matchId}", async (string matchId, HttpRequest request, VodService vods, LeagueDbContext db, CancellationToken ct) =>
{
    if (!await db.Matches.AsNoTracking().AnyAsync(m => m.Id == matchId, ct)) return Results.NotFound();
    if (vods.TargetPath(matchId, "vod.mp4") is not { } target) return Results.BadRequest();

    // A 1440p60 game runs to ~3GB; lift the body cap accordingly.
    request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
        .MaxRequestBodySize = 8L * 1024 * 1024 * 1024;

    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    var temp = target + ".tmp";
    await using (var file = File.Create(temp))
    {
        await request.Body.CopyToAsync(file, ct);
    }
    File.Move(temp, target, overwrite: true);
    return Results.Ok(new { bytes = new FileInfo(target).Length });
});

// Chunked variant of the mp4 upload: Cloudflare caps request bodies around
// 100MB, so a multi-GB VOD arrives as ordered 64MB pieces appended at their
// offset, then an atomic commit that checks the assembled size.
recorder.MapPut("/vods/{matchId}/chunk", async (string matchId, long offset, HttpRequest request, VodService vods, LeagueDbContext db, CancellationToken ct) =>
{
    if (!await db.Matches.AsNoTracking().AnyAsync(m => m.Id == matchId, ct)) return Results.NotFound();
    if (vods.TargetPath(matchId, "vod.mp4.part") is not { } part) return Results.BadRequest();

    request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
        .MaxRequestBodySize = 256L * 1024 * 1024;

    Directory.CreateDirectory(Path.GetDirectoryName(part)!);
    await using var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
    if (offset > file.Length) return Results.Conflict(new { expected = file.Length });
    file.Seek(offset, SeekOrigin.Begin);
    await request.Body.CopyToAsync(file, ct);
    return Results.Ok(new { length = file.Length });
});

recorder.MapPost("/vods/{matchId}/commit", (string matchId, long size, VodService vods) =>
{
    if (vods.TargetPath(matchId, "vod.mp4.part") is not { } part || !File.Exists(part)) return Results.NotFound();
    if (new FileInfo(part).Length != size)
    {
        File.Delete(part); // wrong size = a chunk went missing; restart clean
        return Results.Conflict(new { error = "assembled size mismatch - upload restarted" });
    }
    File.Move(part, vods.TargetPath(matchId, "vod.mp4")!, overwrite: true);
    return Results.Ok();
});

// Sidecar pieces (small): recording metadata, input telemetry, thumbnail.
// Accepted for any known match WITHOUT requiring the mp4 - in the
// YouTube-hosted mode these are the only bytes the tracker ever stores.
recorder.MapPut("/vods/{matchId}/{file}", async (string matchId, string file, HttpRequest request, VodService vods, LeagueDbContext db, CancellationToken ct) =>
{
    var name = file switch
    {
        "meta" => "meta.json",
        "events" => "events.csv.gz",
        "thumb" => "thumb.jpg",
        _ => null,
    };
    if (name is null || !await db.Matches.AsNoTracking().AnyAsync(m => m.Id == matchId, ct)) return Results.NotFound();
    var target = vods.TargetPath(matchId, name)!;
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    var temp = target + ".tmp";
    await using (var f = File.Create(temp))
    {
        await request.Body.CopyToAsync(f, ct);
    }
    File.Move(temp, target, overwrite: true);
    // Telemetry replaced = derived series stale; recomputed on next read.
    if (name is "events.csv.gz" && vods.TargetPath(matchId, "apm.json") is { } apm && File.Exists(apm)) File.Delete(apm);
    return Results.Ok();
});

// --- Full-game renders (opt-in per match; retention-swept unless kept) ----------

media.MapGet("/matches/{id}/fullgame/status", (string id, FullGameService full, RenderLeaseService leases) =>
    Results.Ok(full.Status(id, leases)));

media.MapGet("/matches/{id}/fullgame", (string id, FullGameService full) =>
    full.VideoPath(id) is { } path
        ? Results.File(path, "video/mp4", enableRangeProcessing: true)
        : Results.NotFound());

owner.MapPost("/matches/{id}/fullgame", (string id, FullGameService full, RenderLeaseService leases) =>
    full.Request(id) is { } error
        ? Results.BadRequest(new { error })
        : Results.Ok(full.Status(id, leases)));

owner.MapPost("/matches/{id}/fullgame/keep", (string id, FullGameService full, RenderLeaseService leases) =>
{
    full.ToggleKeep(id);
    return Results.Ok(full.Status(id, leases));
});

owner.MapDelete("/matches/{id}/fullgame", (string id, FullGameService full) =>
{
    full.Delete(id);
    return Results.Ok();
});

// Disk usage per artifact family - keeps the storage cost of renders visible.
owner.MapGet("/storage", (DataPaths paths) =>
{
    static double DirMb(string dir) => Directory.Exists(dir)
        ? Math.Round(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024.0 / 1024.0, 1)
        : 0;
    return Results.Ok(new
    {
        RawGamesMb = DirMb(Path.Combine(paths.DataDir, "games")),
        ReplaysMb = DirMb(Path.Combine(paths.DataDir, "replays")),
        ClipsMb = DirMb(Path.Combine(paths.DataDir, "clips")),
        FullGamesMb = DirMb(Path.Combine(paths.DataDir, "fullgames")),
        VodsMb = DirMb(Path.Combine(paths.DataDir, "vods")),
        DatabaseMb = File.Exists(Path.Combine(paths.DataDir, "leaguetracker.db"))
            ? Math.Round(new FileInfo(Path.Combine(paths.DataDir, "leaguetracker.db")).Length / 1024.0 / 1024.0, 1)
            : 0,
    });
});

// --- Render jobs (served to the render agent on the gaming PC) -------------------

renderRead.MapGet("/render/queue", async (ClipService clips, FullGameService full, RenderLeaseService leases, CancellationToken ct) =>
    Results.Ok((await clips.QueueAsync(leases, ct)).Concat(await full.QueueRowsAsync(leases, ct))));

// Claim the next renderable job. Clip jobs first (cheap, automatic, serve the
// review loop), then explicit full-game requests. The plan manifest is written
// at claim time so uploads can be validated against it.
render.MapPost("/render/next", async (AccountContext acct, ClipService clips, FullGameService full, RenderLeaseService leases,
    ReplayArchiveService replays, VodService vods, LeagueDbContext db, Caller caller, CancellationToken ct = default) =>
{
    // The lease is held by the key that authenticated - never a name the
    // caller chose (audit T-M1).
    var agent = caller.Agent!.Id;
    // The agent locks the replay camera onto this player (names as they were at
    // game time, from the stored participant row).
    async Task<(string? Name, string? Champion)> CameraTargetAsync(string matchId)
    {
        var me = await db.Participants.AsNoTracking()
            .Where(p => p.MatchId == matchId && p.IsMe)
            .Select(p => new { p.RiotId, p.Champion })
            .FirstOrDefaultAsync(ct);
        return (me?.RiotId is { Length: > 0 } riotId ? riotId.Split('#')[0] : null, me?.Champion);
    }

    var archived = replays.ArchivedMatchIds();
    var candidates = await db.Matches.AsNoTracking()
        .Where(m => archived.Contains(m.Id) && m.HasTimeline)
        .OrderByDescending(m => m.GameEndUtc)
        .Select(m => m.Id)
        .ToListAsync(ct);

    foreach (var matchId in candidates)
    {
        if (clips.FailReason(matchId) is not null || leases.IsLeased($"clips:{matchId}")) continue;
        // A match with VOD review data (recorded mp4 or a YouTube link) earns
        // only its "fight" windows - the team fights the player was NOT in,
        // which the VOD's own POV can never show. Their kill/death windows
        // would duplicate footage already on YouTube. Matches without VOD
        // data (agentless trackers, unrecorded queues, failed captures)
        // render everything, and explicit full-game requests below always run.
        var vodCovered = vods.HasVod(matchId) || vods.ReadLink(matchId) is not null;
        // The saved plan is the manifest existing clips were rendered against
        // - recomputing could renumber windows and mislabel surviving files.
        var plan = await clips.LoadPlanAsync(matchId, ct) ?? await clips.PlanAsync(matchId, ct);
        if (plan is not { Windows.Count: > 0 }) continue;
        // Only windows without an mp4: deleting a single bad clip on the match
        // page re-renders just that window, keeping the good ones.
        var missing = plan.Windows.Where(w => clips.ClipPath(matchId, w.Index) is null
            && (!vodCovered || w.Kind is "fight")).ToList();
        if (missing is not { Count: > 0 }) continue;
        if (!leases.TryClaim($"clips:{matchId}", agent)) continue;
        await clips.SavePlanAsync(plan, ct);
        var (myName, myChampion) = await CameraTargetAsync(matchId);
        return Results.Ok(new
        {
            Kind = "clips",
            plan.MatchId, plan.GameVersion, plan.DurationSec,
            ReplayUrl = $"/api/a/{acct.UrlSegment}/matches/{plan.MatchId}/replay",
            MyName = myName,
            MyChampion = myChampion,
            Windows = missing,
        });
    }

    foreach (var matchId in full.PendingRequests())
    {
        if (!archived.Contains(matchId) || leases.IsLeased($"full:{matchId}")) continue;
        var match = await db.Matches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is null || !leases.TryClaim($"full:{matchId}", agent)) continue;
        var (myName, myChampion) = await CameraTargetAsync(matchId);
        return Results.Ok(new
        {
            Kind = "full",
            MatchId = matchId, match.GameVersion, match.DurationSec,
            ReplayUrl = $"/api/a/{acct.UrlSegment}/matches/{matchId}/replay",
            MyName = myName,
            MyChampion = myChampion,
            Windows = new[] { new ClipWindow(0, 0, (int)match.DurationSec, "full", []) },
        });
    }

    return Results.NoContent();
});

render.MapPut("/render/{matchId}/full", async (string matchId, HttpRequest request, FullGameService full, LeagueDbContext db, CancellationToken ct) =>
{
    if (full.VideoTargetPath(matchId) is not { } target) return Results.NotFound();
    if (!await db.Matches.AsNoTracking().AnyAsync(m => m.Id == matchId, ct)) return Results.NotFound();

    // A full game runs to ~500MB; lift the body cap accordingly.
    request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
        .MaxRequestBodySize = 4L * 1024 * 1024 * 1024;

    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    var temp = target + ".tmp";
    await using (var file = File.Create(temp))
    {
        await request.Body.CopyToAsync(file, ct);
    }
    File.Move(temp, target, overwrite: true);
    return Results.Ok(new { bytes = new FileInfo(target).Length });
});

render.MapPut("/render/{matchId}/clips/{index:int}", async (string matchId, int index, HttpRequest request, ClipService clips, CancellationToken ct) =>
{
    var plan = await clips.LoadPlanAsync(matchId, ct);
    if (plan is null || index < 0 || index >= plan.Windows.Count) return Results.NotFound();

    // Clips run tens of MB; lift the default 30MB body cap for this request only.
    request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
        .MaxRequestBodySize = 512L * 1024 * 1024;

    var target = clips.ClipTargetPath(matchId, index);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    var temp = target + ".tmp";
    await using (var file = File.Create(temp))
    {
        await request.Body.CopyToAsync(file, ct);
    }
    File.Move(temp, target, overwrite: true);
    return Results.Ok(new { index, bytes = new FileInfo(target).Length });
});

render.MapPost("/render/{matchId}/complete", (string matchId, RenderLeaseService leases, ClipService clips, FullGameService full, string kind = "clips") =>
{
    if (kind is "full") full.CompleteRequest(matchId);
    else clips.ClearFailed(matchId);
    leases.Release($"{kind}:{matchId}");
    return Results.Ok();
});

render.MapPost("/render/{matchId}/fail", async (string matchId, HttpRequest request, RenderLeaseService leases, ClipService clips, FullGameService full, string kind = "clips", CancellationToken ct = default) =>
{
    using var reader = new StreamReader(request.Body);
    var error = await reader.ReadToEndAsync(ct);
    error = error is { Length: > 0 } ? error.Trim() : "unknown";
    if (kind is "full") full.MarkFailed(matchId, error);
    else await clips.MarkFailedAsync(matchId, error, ct);
    leases.Release($"{kind}:{matchId}");
    return Results.Ok();
});

// A restarting agent frees the leases a dead previous incarnation of itself
// took to its grave, so interrupted jobs re-queue immediately instead of
// waiting out the 30-minute lease.
render.MapPost("/render/release-stale", (Caller caller, RenderLeaseService leases) =>
    Results.Ok(new { released = leases.ReleaseAgent(caller.Agent!.Id) }));


// Re-queues the match: clears the failed marker AND deletes any existing
// clips, so both failed and badly-rendered matches get picked up again.
owner.MapPost("/render/{matchId}/dismiss", (string matchId, ClipService clips, FullGameService full, string kind = "clips") =>
    (kind is "full" ? full.Dismiss(matchId) : clips.Dismiss(matchId)) ? Results.Ok() : Results.NotFound());

owner.MapPost("/render/{matchId}/retry", (string matchId, ClipService clips, FullGameService full, string kind = "clips") =>
{
    if (kind is "full") full.Request(matchId);
    else
    {
        clips.ClearFailed(matchId);
        clips.DeleteClips(matchId);
    }
    return Results.Ok();
});

read.MapGet("/matches/{id}", async (AccountContext acct, string id, LeagueDbContext db, ReplayArchiveService replays, CancellationToken ct) =>
{
    // Four collection includes in one query = a cartesian product across all of
    // them (millions of rows per match on SQLite). Split runs one query each.
    var match = await db.Matches.AsNoTracking()
        .AsSplitQuery()
        .Include(m => m.Participants.OrderBy(p => p.ParticipantId))
        .Include(m => m.DeathEvents.OrderBy(d => d.TimeSec)).ThenInclude(d => d.DamageInstances)
        .Include(m => m.ObjectiveEvents.OrderBy(o => o.TimeSec))
        .Include(m => m.ItemEvents.OrderBy(i => i.TimeSec))
        .FirstOrDefaultAsync(m => m.Id == id, ct);
    if (match is null) return Results.NotFound();

    var champByPid = match.Participants.ToDictionary(p => p.ParticipantId, p => p.Champion);
    // The player's killing blows. DeathEvents carry the analytics; kills only
    // need to exist as review moments (the VOD card jumps to them), so
    // timestamp + victim is the whole story.
    var myPid = match.Participants.FirstOrDefault(p => p.IsMe)?.ParticipantId;
    var myKills = myPid is not { } pid
        ? []
        : await db.KillEvents.AsNoTracking()
            .Where(k => k.MatchId == id && k.KillerParticipantId == pid)
            .OrderBy(k => k.TimeSec)
            .ToListAsync(ct);
    object TeamObjectives(bool mine) => new
    {
        Towers = match.ObjectiveEvents.Count(o => o.Kind is "TOWER" && o.ByMyTeam == mine),
        Inhibitors = match.ObjectiveEvents.Count(o => o.Kind is "INHIBITOR" && o.ByMyTeam == mine),
        Dragons = match.ObjectiveEvents.Count(o => o.Kind is "DRAGON" && o.ByMyTeam == mine),
        Barons = match.ObjectiveEvents.Count(o => o.Kind is "BARON" && o.ByMyTeam == mine),
        Heralds = match.ObjectiveEvents.Count(o => o.Kind is "HERALD" && o.ByMyTeam == mine),
        Grubs = match.ObjectiveEvents.Count(o => o.Kind is "GRUBS" && o.ByMyTeam == mine),
        Atakhan = match.ObjectiveEvents.Count(o => o.Kind is "ATAKHAN" && o.ByMyTeam == mine),
    };

    return Results.Ok(new
    {
        Summary = MatchListItem(match, hasReplay: replays.PathFor(match.Id) is not null, hideLp: acct.Current.HideLp),
        match.RanksAtGameTime,
        MySide = match.Participants.FirstOrDefault(p => p.IsMe)?.TeamId == 100 ? "Blue" : "Red",
        TeamObjectives = new { Ally = TeamObjectives(true), Enemy = TeamObjectives(false) },
        SkillOrder = match.SkillOrder is { Length: > 0 } ? match.SkillOrder.Split(',').Select(int.Parse).ToArray() : [],
        Laning = new
        {
            match.CsAt10, match.CsAt15,
            match.LaneGoldDiff10, match.LaneXpDiff10, match.LaneCsDiff10,
            match.LaneGoldDiff15, match.LaneXpDiff15, match.LaneCsDiff15,
            match.FirstToLevel2,
            Checkpoints = match.LaneDiffsJson is { Length: > 0 }
                ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(match.LaneDiffsJson)
                : (object?)null,
        },
        Wards = new { match.WardsPlaced, match.WardsKilled, match.ControlWards },
        Macro = new
        {
            match.AvgUnspentGold, match.MaxUnspentGold,
            match.FirstWardSec, match.FirstControlWardSec, match.WardsFirst10,
            match.Level6LeadSec, match.Level11LeadSec, match.Level16LeadSec,
            match.FriendlyEpicObjectives, match.ObjectivesPresentFor,
        },
        Participants = match.Participants.Select(p => new
        {
            p.ParticipantId, p.RiotId, p.Champion, p.Position, p.TeamId, p.IsMe, p.IsAlly, p.Win,
            p.Kills, p.Deaths, p.Assists, p.Cs, p.Gold, p.DamageToChampions, p.VisionScore, p.ChampLevel,
            Tier = acct.Current.HideLp ? null : p.Tier,
            Division = acct.Current.HideLp ? null : p.Division,
            Lp = acct.Current.HideLp ? null : p.Lp,
            SeasonWins = acct.Current.HideLp ? null : p.SeasonWins,
            SeasonLosses = acct.Current.HideLp ? null : p.SeasonLosses,
            RankValue = acct.Current.HideLp ? null : p.RankValue,
            RankQueue = acct.Current.HideLp ? null : p.RankQueue,
            RankLabel = acct.Current.HideLp || p.Tier is null ? null : $"{p.Tier} {p.Division} {p.Lp} LP",
            WinratePct = !acct.Current.HideLp && p is { SeasonWins: int w, SeasonLosses: int l } && w + l > 0 ? Math.Round(100.0 * w / (w + l), 1) : (double?)null,
            p.Summoner1Id, p.Summoner2Id, p.PrimaryStyleId, p.SubStyleId, p.KeystoneId, p.Items,
            p.SkillshotsHit, p.SkillshotsDodged, p.SkillshotDodgesLateWindow, p.KillParticipation,
            p.PerksJson, p.PingsJson,
            p.Spell1Casts, p.Spell2Casts, p.Spell3Casts, p.Spell4Casts, p.Summoner1Casts, p.Summoner2Casts,
        }),
        Deaths = match.DeathEvents.Select(d => new
        {
            d.TimeSec, GameTime = $"{d.TimeSec / 60:00}:{d.TimeSec % 60:00}",
            d.X, d.Y, d.KilledBy, d.AssistedBy, d.DamageFrom, d.EnemiesOnYou,
            d.Bounty, d.Shutdown, d.MyLevel, d.MyTotalGold, d.MyCs,
            d.EnemiesNearDeath, d.AlliesNearDeath, d.NearestAllyDist,
            d.TotalDamageReceived, d.DamageInstanceCount, d.TopSource, d.TopSourceShare,
            d.SecondsAfterObjective, d.ObjectiveBefore, d.Zone,
            d.FollowTeammate, d.FollowTeammateRole, d.FollowTeammateCaughtBy, d.FollowSecondsAfter,
            d.FollowDistance, d.FollowAlliesDownBefore, d.FollowPureLoss, d.FollowTeamGoldDiff,
            DamageInstances = d.DamageInstances.Select(i => new { i.Source, i.SpellName, i.Physical, i.Magic, i.TrueDamage, i.Total }),
        }),
        Kills = myKills.Select(k => new
        {
            k.TimeSec, GameTime = $"{k.TimeSec / 60:00}:{k.TimeSec % 60:00}",
            Victim = champByPid.GetValueOrDefault(k.VictimParticipantId),
        }),
        // The analyzer's fight clusters (duel/skirmish/teamfight, either
        // team's, participated or not) - the VOD card marks them as jump
        // points so fights the player never touched are still reviewable.
        Fights = match.FightsJson is { Length: > 0 }
            ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(match.FightsJson)
            : (object?)null,
        Objectives = match.ObjectiveEvents.Select(o => new
        {
            o.TimeSec, GameTime = $"{o.TimeSec / 60:00}:{o.TimeSec % 60:00}",
            o.Kind, o.SubKind, o.ByMyTeam,
            Killer = champByPid.GetValueOrDefault(o.KillerParticipantId),
        }),
        ItemEvents = match.ItemEvents.Select(i => new { i.TimeSec, i.Kind, i.ItemId }),
    });
});

// Collapse-focused death analytics over the recent ranked games with timelines.
// Deliberately centred on collapse count and contest quality, not KDA cosmetics.
read.MapGet("/analytics/summary", async (LeagueDbContext db, int lastN = 20, CancellationToken ct = default) =>
    Results.Ok(await Reports.AnalyticsSummaryAsync(db, lastN, ct)));

// Per-player cumulative curves from the raw timeline (gold/cs/damage/xp).
read.MapGet("/matches/{id}/series", async (string id, TimelineSeriesService series, CancellationToken ct) =>
    await series.GetAsync(id, ct) is { } result ? Results.Ok(result) : Results.NoContent());

// The Lens: coaching scores for the recent window vs the player's own history,
// optionally scoped to one role (TOP/JUNGLE/MIDDLE/BOTTOM/UTILITY).
read.MapGet("/lens", async (LensService lens, int window = 20, int? days = null, string? role = null, CancellationToken ct = default) =>
    await lens.GetAsync(window, days, role, ct) is { } result ? Results.Ok(result) : Results.NoContent());

// Ladder percentiles (Challenges-V1) - how the player ranks vs everyone, the
// external benchmark the wins-vs-losses analysis can't provide.
read.MapGet("/challenges/percentiles", async (ChallengesBenchmarkService svc, CancellationToken ct) =>
    await svc.GetAsync(ct) is { } result ? Results.Ok(result) : Results.NoContent());

// The Fundamentals ladder: curriculum skills pinned to rank tiers, each scored
// by self-percentile and anchored on Riot's own challenge levels where mapped.
read.MapGet("/fundamentals", async (FundamentalsService svc, int window = 20, int? days = null, string? role = null, CancellationToken ct = default) =>
    await svc.GetAsync(window, days, role, ct) is { } result ? Results.Ok(result) : Results.NoContent());

// The three questions, answered per game and blind to the result: out-dueled
// my lane / fights bought the map / stepped with the enemy accounted for.
read.MapGet("/matches/{id}/review", async (string id, ReviewService svc, CancellationToken ct) =>
    await svc.GetAsync(id, ct) is { } result ? Results.Ok(result) : Results.NoContent());

// The between-games review: the moments the player was in, as replay
// timestamps, for the agent to drive the game client through.
read.MapGet("/matches/{id}/reel", async (string id, ReviewReelService svc, CancellationToken ct) =>
    await svc.GetAsync(id, ct) is { } reel ? Results.Ok(reel) : Results.NotFound());

// Verdict triples for a page of matches (the list rows' process chips).
read.MapGet("/reviews", async (string ids, ReviewService svc, CancellationToken ct) =>
    Results.Ok(await svc.VerdictsAsync(
        ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(100).ToArray(), ct)));

// The dashboard aggregate: coach-style stats over recent ranked games.
// lastGames takes precedence over days; neither = whole history.
read.MapGet("/stats", async (AccountContext acct, LeagueDbContext db, int? days, int? lastGames, CancellationToken ct) =>
    Results.Ok(await Reports.StatsAsync(db, days, lastGames, acct.Current.HideLp, ct)));
owner.MapPost("/ranks/backfill", (AccountScopes scopes, AccountContext acct, JobStatusService jobs, int days = 7) =>
{
    if (!jobs.TryStart("rank-backfill")) return Results.Conflict(jobs.Snapshot());
    _ = Task.Run(async () =>
    {
        using var scope = scopes.Create(acct.Current);
        try
        {
            await scope.ServiceProvider.GetRequiredService<HistorySyncService>().BackfillRanksAsync(days, CancellationToken.None);
        }
        catch
        {
            // Already logged and surfaced via job status.
        }
    });
    return Results.Accepted($"/api/a/{acct.UrlSegment}/jobs/status", jobs.Snapshot());
});

owner.MapPost("/analytics/reprocess", (AccountScopes scopes, AccountContext acct, JobStatusService jobs) =>
{
    if (!jobs.TryStart("reprocess")) return Results.Conflict(jobs.Snapshot());
    _ = Task.Run(async () =>
    {
        using var scope = scopes.Create(acct.Current);
        try
        {
            await scope.ServiceProvider.GetRequiredService<AnalyticsReprocessService>().ReprocessAsync(CancellationToken.None);
        }
        catch
        {
            // Already logged and surfaced via job status.
        }
    });
    return Results.Accepted($"/api/a/{acct.UrlSegment}/jobs/status", jobs.Snapshot());
});

// --- Stop-loss ------------------------------------------------------------------
// Evidence for the tilt guard: how the NEXT ranked game historically went after
// N straight same-session losses (a session chains games ending <3h apart),
// plus the current tail streak. Winrate-only - no LP, so it works on every
// instance and doesn't need attribution.
read.MapGet("/stoploss", async (LeagueDbContext db, CancellationToken ct) =>
{
    var games = await db.Matches.AsNoTracking()
        .Where(m => m.IsRanked && m.DurationSec >= 300)
        .OrderBy(m => m.GameEndUtc)
        .Select(m => new { m.GameEndUtc, m.Win })
        .ToListAsync(ct);

    const double sessionGapMin = 180;
    var total = new int[4];   // index = losses immediately before the game, capped at 3+
    var wins = new int[4];
    var streak = 0;
    DateTime? prevEnd = null;
    foreach (var g in games)
    {
        if (prevEnd is { } p && (g.GameEndUtc - p).TotalMinutes > sessionGapMin) streak = 0;
        var idx = Math.Min(streak, 3);
        total[idx]++;
        if (g.Win) wins[idx]++;
        streak = g.Win ? 0 : streak + 1;
        prevEnd = g.GameEndUtc;
    }

    var minutesSince = prevEnd is { } last ? (int?)Math.Round((DateTime.UtcNow - last).TotalMinutes) : null;
    return Results.Ok(new
    {
        Streak = streak,
        LastGameEndUtc = prevEnd,
        MinutesSinceLastGame = minutesSince,
        SessionActive = minutesSince is { } m && m <= sessionGapMin,
        NextGame = Enumerable.Range(0, 4).Select(i => new
        {
            AfterLosses = i,
            Games = total[i],
            WinRate = total[i] > 0 ? (double?)Math.Round(100.0 * wins[i] / total[i]) : null,
        }).ToList(),
    });
});

// --- LP -------------------------------------------------------------------------

read.MapGet("/lp/history", async (AccountContext acct, LeagueDbContext db, string? queue, CancellationToken ct) =>
{
    if (acct.Current.HideLp) return Results.Ok(Array.Empty<object>());
    var query = db.LpSnapshots.AsNoTracking();
    if (queue is not null) query = query.Where(s => s.Queue == queue);
    var rows = await query.OrderBy(s => s.TimestampUtc).ToListAsync(ct);
    return Results.Ok(rows.Select(s => new
    {
        s.TimestampUtc, s.Queue, s.Tier, s.Division, s.Lp, s.Wins, s.Losses, s.RankValue,
        Label = $"{s.Tier} {s.Division} {s.Lp} LP",
    }));
});

read.MapGet("/lp/per-game", async (AccountContext acct, LeagueDbContext db, CancellationToken ct) =>
{
    var rows = await db.Matches.AsNoTracking()
        .Where(m => m.IsRanked)
        .OrderByDescending(m => m.GameEndUtc)
        .Take(200)
        .ToListAsync(ct);
    return Results.Ok(rows.Select(m => new
    {
        m.Id, m.GameEndUtc, m.QueueName, m.Champion, m.Position, m.Win,
        Kda = $"{m.Kills}/{m.Deaths}/{m.Assists}",
        LpBefore = acct.Current.HideLp ? null : m.LpBefore,
        LpAfter = acct.Current.HideLp ? null : m.LpAfter,
        LpChange = acct.Current.HideLp ? null : m.LpChange,
    }));
});

// --- Background jobs: history backfill + import of the PowerShell exports --------

owner.MapPost("/sync/history", (AccountScopes scopes, AccountContext acct, JobStatusService jobs,
    int rankedTarget = 0, int maxMatches = 0, bool timeline = true, bool ranks = true) =>
{
    if (!jobs.TryStart("history-sync")) return Results.Conflict(jobs.Snapshot());
    _ = Task.Run(async () =>
    {
        using var scope = scopes.Create(acct.Current);
        try
        {
            await scope.ServiceProvider.GetRequiredService<HistorySyncService>()
                .SyncAsync(rankedTarget, maxMatches, timeline, ranks, CancellationToken.None);
        }
        catch
        {
            // Already logged and surfaced via job status.
        }
    });
    return Results.Accepted($"/api/a/{acct.UrlSegment}/jobs/status", jobs.Snapshot());
});

owner.MapPost("/import", (string path, AccountScopes scopes, AccountContext acct, JobStatusService jobs, IWebHostEnvironment env) =>
{
    // The folder must sit under this account's data folder or the deployment's
    // import mount: an arbitrary server path was a directory read and an
    // existence oracle (audit T-B6).
    var full = Path.GetFullPath(path);
    var allowedRoots = new[] { Path.GetFullPath(acct.DataDir), Path.GetFullPath("/imports"), Path.GetFullPath(Path.Combine(env.ContentRootPath, "../../imports")) };
    if (!allowedRoots.Any(root => full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)))
    {
        return Results.BadRequest(new { error = "Import folders must be inside this account's data folder or the /imports mount" });
    }
    if (!Directory.Exists(full)) return Results.BadRequest(new { error = $"Folder not found: {path}" });
    path = full;
    if (!jobs.TryStart("import")) return Results.Conflict(jobs.Snapshot());
    _ = Task.Run(async () =>
    {
        using var scope = scopes.Create(acct.Current);
        try
        {
            await scope.ServiceProvider.GetRequiredService<ImportService>().ImportFolderAsync(path, CancellationToken.None);
        }
        catch
        {
            // Already logged and surfaced via job status.
        }
    });
    return Results.Accepted($"/api/a/{acct.UrlSegment}/jobs/status", jobs.Snapshot());
});

owner.MapGet("/jobs/status", (JobStatusService jobs) => Results.Ok(jobs.Snapshot()));

// --- Exports (PowerShell-tooling-compatible CSV shapes + an everything-bundle) --

owner.MapGet("/export/matches.csv", async (AccountContext acct, LeagueDbContext db, ReviewService reviews, CancellationToken ct) =>
    CsvFile("matches-summary.csv", await Reports.MatchesCsvAsync(db, reviews, acct.Current.HideLp, ct)));

owner.MapGet("/export/deaths.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("deaths.csv", await Reports.DeathsCsvAsync(db, ct)));

owner.MapGet("/export/ranks.csv", async (AccountContext acct, LeagueDbContext db, CancellationToken ct) =>
    CsvFile("ranks.csv", await Reports.RanksCsvAsync(db, acct.Current.HideLp, ct)));

owner.MapGet("/export/lp-history.csv", async (AccountContext acct, LeagueDbContext db, CancellationToken ct) =>
    CsvFile("lp-history.csv", await Reports.LpHistoryCsvAsync(db, acct.Current.HideLp, ct)));

owner.MapGet("/export/challenges.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("challenges.csv", await Reports.ChallengesCsvAsync(db, ct)));

owner.MapGet("/export/lane-checkpoints.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("lane-checkpoints.csv", await Reports.LaneCheckpointsCsvAsync(db, ct)));

owner.MapGet("/export/objectives.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("objectives.csv", await Reports.ObjectivesCsvAsync(db, ct)));

// Everything in one download: every CSV the screens are built from, plus the
// dashboard aggregate over all games as machine-readable JSON.
owner.MapGet("/export/all.zip", async (AccountContext acct, LeagueDbContext db, ReviewService reviews, LpService lp, TrackedPlayerService player, CancellationToken ct) =>
{
    var summary = new
    {
        player.RiotId,
        ExportedAtUtc = DateTime.UtcNow,
        Matches = await db.Matches.CountAsync(ct),
        Deaths = await db.Deaths.CountAsync(ct),
        Patches = await Reports.PatchesAsync(db, ct),
        Ranks = new[] { await lp.GetLatestAsync("Solo/Duo", ct), await lp.GetLatestAsync("Flex", ct) }
            .Where(s => s is not null && !acct.Current.HideLp)
            .Select(s => new { s!.Queue, s.Tier, s.Division, s.Lp, s.Wins, s.Losses }),
        Files = new[]
        {
            "matches-summary.csv - one row per game, all headline + laning + macro columns",
            "challenges.csv - Riot's full per-game challenges block (strengths & weaknesses source)",
            "lane-checkpoints.csv - gold/xp/cs/level diff + item race at 10/15/20/25",
            "ranks.csv - all 10 participants per game: score, rank, loadout",
            "deaths.csv - every death: collapse, follow-in, damage, objective context",
            "objectives.csv - objective timeline per game",
            "lp-history.csv - LP snapshots over time",
            "dashboard.json - the full dashboard aggregate over all ranked games",
        },
    };
    // The dashboard's computed views (overall, lane state, strengths/weaknesses,
    // champion/role splits, follow-in) over the entire ranked history.
    var dashboard = await Reports.StatsAsync(db, days: null, lastGames: null, acct.Current.HideLp, ct);

    using var ms = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        async Task AddAsync(string name, string content)
        {
            await using var entry = zip.CreateEntry(name).Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
        }
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        await AddAsync("matches-summary.csv", await Reports.MatchesCsvAsync(db, reviews, acct.Current.HideLp, ct));
        await AddAsync("challenges.csv", await Reports.ChallengesCsvAsync(db, ct));
        await AddAsync("lane-checkpoints.csv", await Reports.LaneCheckpointsCsvAsync(db, ct));
        await AddAsync("ranks.csv", await Reports.RanksCsvAsync(db, acct.Current.HideLp, ct));
        await AddAsync("deaths.csv", await Reports.DeathsCsvAsync(db, ct));
        await AddAsync("objectives.csv", await Reports.ObjectivesCsvAsync(db, ct));
        await AddAsync("lp-history.csv", await Reports.LpHistoryCsvAsync(db, acct.Current.HideLp, ct));
        await AddAsync("dashboard.json", System.Text.Json.JsonSerializer.Serialize(dashboard, jsonOpts));
        await AddAsync("summary.json", System.Text.Json.JsonSerializer.Serialize(summary, jsonOpts));
    }
    return Results.File(ms.ToArray(), "application/zip", $"leaguetracker-export-{DateTime.Now:yyyyMMdd-HHmm}.zip");
});
}


static object MatchListItem(Match m, string? items = null, int? summoner1Id = null, int? summoner2Id = null, bool hasReplay = false,
    string? myCompanion = null, string? enemyCompanion = null, string? companionRole = null, bool hideLp = false) => new
{
    Items = items,
    Summoner1Id = summoner1Id,
    Summoner2Id = summoner2Id,
    HasReplay = hasReplay,
    MyCompanion = myCompanion,
    EnemyCompanion = enemyCompanion,
    CompanionRole = companionRole,
    m.Id, m.QueueId, m.QueueName, m.IsRanked, m.GameMode,
    Patch = string.Join('.', m.GameVersion.Split('.').Take(2)),
    Date = m.GameCreationUtc, m.GameEndUtc,
    DurationMin = Math.Round(m.DurationSec / 60, 1),
    m.Champion, m.Position, m.Win, m.Kills, m.Deaths, m.Assists,
    Kda = m.Deaths == 0 ? (m.Kills + m.Assists > 0 ? "Perfect" : "0") : Math.Round((m.Kills + m.Assists) / (double)m.Deaths, 2).ToString(),
    m.Cs, m.Gold, m.DamageToChampions, m.VisionScore, m.ChampLevel, m.HasTimeline,
    AvgAllyRank = !hideLp && m.AvgAllyRankValue is { } ally ? RankMath.ToLabel(ally) : null,
    AvgEnemyRank = !hideLp && m.AvgEnemyRankValue is { } enemy ? RankMath.ToLabel(enemy) : null,
    RankGapLp = !hideLp && m is { AvgAllyRankValue: { } a, AvgEnemyRankValue: { } e } ? (int?)Math.Round(e - a) : null,
    AllyRanksKnown = hideLp ? 0 : m.AllyRanksKnown,
    EnemyRanksKnown = hideLp ? 0 : m.EnemyRanksKnown,
    m.RanksAtGameTime,
    LpChange = hideLp ? null : m.LpChange,
    LpBefore = hideLp ? null : m.LpBefore,
    LpAfter = hideLp ? null : m.LpAfter,
    m.TimeInEnemyHalfPct, m.AvgNearestAllyDist,
    m.SkillshotsHit, m.SkillshotsDodged,
    m.OpponentChampion, m.EnemyJungler, m.AllyJungler, m.CsAt10, m.LaneGoldDiff10, m.KillParticipation, m.SoloKills,
    IsRemake = m.DurationSec < 300,
};

static IResult CsvFile(string fileName, string csv) =>
    Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);

public sealed record AddAccountRequest(string RiotId, string Region, string? DisplayName);

public sealed record AccountSettingsRequest(bool? MediaPublic, bool? HideLp, string? DisplayName);

public sealed record EnrollRequest(string Key, string? Name, string? Machine, string? Code);
