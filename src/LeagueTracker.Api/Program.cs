using System.Text;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Match = LeagueTracker.Api.Data.Match;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "LeagueTracker");
builder.Services.Configure<RiotOptions>(builder.Configuration.GetSection("Riot"));

var riotOptions = builder.Configuration.GetSection("Riot").Get<RiotOptions>() ?? new RiotOptions();
var dataDir = Path.IsPathRooted(riotOptions.DataDir) ? riotOptions.DataDir : Path.Combine(builder.Environment.ContentRootPath, riotOptions.DataDir);
Directory.CreateDirectory(dataDir);
builder.Services.AddDbContext<LeagueDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "leaguetracker.db")}"));

builder.Services.AddSingleton<RiotRateLimiter>();
builder.Services.AddSingleton<IRiotKeyProvider, RiotKeyProvider>();
builder.Services.AddSingleton<RankCache>();
builder.Services.AddSingleton<JobStatusService>();
builder.Services.AddTransient<RiotRateLimitHandler>();
// Generous timeout: the rate limiter paces requests INSIDE the handler, so a
// burst legitimately waits out the key's 2-minute budget before sending.
builder.Services.AddHttpClient<RiotApiClient>(c => c.Timeout = TimeSpan.FromMinutes(10))
    .AddHttpMessageHandler<RiotRateLimitHandler>();

builder.Services.AddSingleton<DataPaths>();
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
builder.Services.AddSingleton<RenderLeaseService>();
builder.Services.AddSingleton<LiveGameState>();
builder.Services.AddHostedService<MatchPollerService>();

// Vite dev server origin; irrelevant in production where the SPA is served by this host.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LeagueDbContext>();
    db.Database.EnsureCreated();
    // Additive columns land via idempotent ALTERs - EnsureCreated never alters an
    // existing table, and wiping the db would cost capture-time-only data (ranks, LP).
    foreach (var alter in new[]
    {
        "ALTER TABLE Matches ADD COLUMN AllyJungler TEXT NULL",
        "ALTER TABLE Matches ADD COLUMN TotalTimeSpentDead INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE Matches ADD COLUMN LongestTimeSpentLiving INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE Matches ADD COLUMN TotalTimeCcDealt INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE Matches ADD COLUMN ChallengesJson TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Matches ADD COLUMN AvgUnspentGold INTEGER NULL",
        "ALTER TABLE Matches ADD COLUMN MaxUnspentGold INTEGER NULL",
        "ALTER TABLE Matches ADD COLUMN FirstWardSec INTEGER NULL",
        "ALTER TABLE Matches ADD COLUMN FirstControlWardSec INTEGER NULL",
        "ALTER TABLE Matches ADD COLUMN WardsFirst10 INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE Matches ADD COLUMN Level6LeadSec INTEGER NULL",
        "ALTER TABLE Matches ADD COLUMN Level11LeadSec INTEGER NULL",
        "ALTER TABLE Matches ADD COLUMN Level16LeadSec INTEGER NULL",
        "ALTER TABLE Matches ADD COLUMN FriendlyEpicObjectives INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE Matches ADD COLUMN ObjectivesPresentFor INTEGER NOT NULL DEFAULT 0",
    })
    {
        try { db.Database.ExecuteSqlRaw(alter); } catch { /* column already exists */ }
    }
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// --- Status ---------------------------------------------------------------------

app.MapGet("/api/status", async (LeagueDbContext db, LpService lp, TrackedPlayerService player, IRiotKeyProvider keys, JobStatusService jobs, ReplayArchiveService replays, CancellationToken ct) =>
{
    var solo = await lp.GetLatestAsync("Solo/Duo", ct);
    var flex = await lp.GetLatestAsync("Flex", ct);
    var hasMatches = await db.Matches.AnyAsync(ct);
    return Results.Ok(new
    {
        player.RiotId,
        ApiKeyConfigured = keys.GetKey() is not null,
        Matches = await db.Matches.CountAsync(ct),
        RankedMatches = await db.Matches.CountAsync(m => m.IsRanked, ct),
        Deaths = await db.Deaths.CountAsync(ct),
        LpSnapshots = await db.LpSnapshots.CountAsync(ct),
        Replays = replays.ArchivedMatchIds().Count,
        Patches = await Reports.PatchesAsync(db, ct),
        DateFrom = hasMatches ? (await db.Matches.MinAsync(m => m.GameCreationUtc, ct)).ToLocalTime().ToString("yyyy-MM-dd") : null,
        DateTo = hasMatches ? (await db.Matches.MaxAsync(m => m.GameCreationUtc, ct)).ToLocalTime().ToString("yyyy-MM-dd") : null,
        Ranks = new[] { solo, flex }.Where(s => s is not null).Select(s => new
        {
            s!.Queue, s.Tier, s.Division, s.Lp, s.Wins, s.Losses, s.RankValue,
            Label = $"{s.Tier} {s.Division} {s.Lp} LP",
            AsOfUtc = s.TimestampUtc,
        }),
        Job = jobs.Snapshot(),
    });
});

// The game being played right now (spectator-v5, refreshed by the poller).
app.MapGet("/api/live", (LiveGameState live) =>
    live.Current is { } g
        ? Results.Ok(new
        {
            g.MatchId, g.QueueId, Queue = RankMath.QueueName(g.QueueId),
            g.StartedUtc, g.DetectedUtc, g.MyChampionId, g.MyTeamId,
            Participants = g.Participants.Select(p => new { p.ChampionId, p.TeamId, p.RiotId, p.IsMe }),
        })
        : Results.NoContent());

// --- Matches --------------------------------------------------------------------

app.MapGet("/api/matches", async (LeagueDbContext db, ReplayArchiveService replays, int page = 1, int pageSize = 20, bool? ranked = null, string? champion = null, CancellationToken ct = default) =>
{
    var query = db.Matches.AsNoTracking();
    if (ranked is not null) query = query.Where(m => m.IsRanked == ranked);
    if (champion is not null) query = query.Where(m => m.Champion == champion);

    var total = await query.CountAsync(ct);
    var items = await query
        .OrderByDescending(m => m.GameEndUtc)
        .Skip((Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 200)).Take(Math.Clamp(pageSize, 1, 200))
        .ToListAsync(ct);

    // My loadout per row (items + summs) for the list's icon strip.
    var ids = items.Select(m => m.Id).ToList();
    var loadouts = (await db.Participants.AsNoTracking()
            .Where(p => ids.Contains(p.MatchId) && p.IsMe)
            .Select(p => new { p.MatchId, p.Items, p.Summoner1Id, p.Summoner2Id })
            .ToListAsync(ct))
        .ToDictionary(p => p.MatchId);

    var archived = replays.ArchivedMatchIds();
    return Results.Ok(new
    {
        total,
        items = items.Select(m => MatchListItem(m,
            loadouts.TryGetValue(m.Id, out var l) ? l.Items : null,
            loadouts.TryGetValue(m.Id, out var l1) ? l1.Summoner1Id : null,
            loadouts.TryGetValue(m.Id, out var l2) ? l2.Summoner2Id : null,
            archived.Contains(m.Id))),
    });
});

// The archived official .rofl for a game (playable in the client on the same patch).
app.MapGet("/api/matches/{id}/replay", (string id, ReplayArchiveService replays) =>
    replays.PathFor(id) is { } path
        ? Results.File(path, "application/octet-stream", $"{id}.rofl")
        : Results.NotFound());

// The planned highlight windows for a game and whether each mp4 has landed yet.
app.MapGet("/api/matches/{id}/clips", async (string id, ClipService clips, CancellationToken ct) =>
{
    var plan = await clips.LoadPlanAsync(id, ct);
    return Results.Ok(plan is null
        ? []
        : plan.Windows.Select(w => new
        {
            w.Index, w.Label, w.StartSec, w.EndSec, w.Events,
            Url = $"/api/matches/{id}/clips/{w.Index}",
            Ready = clips.ClipPath(id, w.Index) is not null,
        }));
});

// Range processing on: the <video> scrub bar needs partial requests.
app.MapGet("/api/matches/{id}/clips/{index:int}", (string id, int index, ClipService clips) =>
    clips.ClipPath(id, index) is { } path
        ? Results.File(path, "video/mp4", enableRangeProcessing: true)
        : Results.NotFound());

// --- Full-game renders (opt-in per match; retention-swept unless kept) ----------

app.MapGet("/api/matches/{id}/fullgame/status", (string id, FullGameService full, RenderLeaseService leases) =>
    Results.Ok(full.Status(id, leases)));

app.MapGet("/api/matches/{id}/fullgame", (string id, FullGameService full) =>
    full.VideoPath(id) is { } path
        ? Results.File(path, "video/mp4", enableRangeProcessing: true)
        : Results.NotFound());

app.MapPost("/api/matches/{id}/fullgame", (string id, FullGameService full, RenderLeaseService leases) =>
    full.Request(id) is { } error
        ? Results.BadRequest(new { error })
        : Results.Ok(full.Status(id, leases)));

app.MapPost("/api/matches/{id}/fullgame/keep", (string id, FullGameService full, RenderLeaseService leases) =>
{
    full.ToggleKeep(id);
    return Results.Ok(full.Status(id, leases));
});

app.MapDelete("/api/matches/{id}/fullgame", (string id, FullGameService full) =>
{
    full.Delete(id);
    return Results.Ok();
});

// Disk usage per artifact family - keeps the storage cost of renders visible.
app.MapGet("/api/storage", (DataPaths paths) =>
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
        DatabaseMb = File.Exists(Path.Combine(paths.DataDir, "leaguetracker.db"))
            ? Math.Round(new FileInfo(Path.Combine(paths.DataDir, "leaguetracker.db")).Length / 1024.0 / 1024.0, 1)
            : 0,
    });
});

// --- Render jobs (served to the render agent on the gaming PC) -------------------

app.MapGet("/api/render/queue", async (ClipService clips, FullGameService full, RenderLeaseService leases, CancellationToken ct) =>
    Results.Ok((await clips.QueueAsync(leases, ct)).Concat(await full.QueueRowsAsync(leases, ct))));

// Claim the next renderable job. Clip jobs first (cheap, automatic, serve the
// review loop), then explicit full-game requests. The plan manifest is written
// at claim time so uploads can be validated against it.
app.MapPost("/api/render/next", async (ClipService clips, FullGameService full, RenderLeaseService leases,
    ReplayArchiveService replays, LeagueDbContext db, string agent = "render-agent", CancellationToken ct = default) =>
{
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
        if (clips.HasClips(matchId) || clips.FailReason(matchId) is not null || leases.IsLeased($"clips:{matchId}")) continue;
        if (await clips.PlanAsync(matchId, ct) is not { Windows.Count: > 0 } plan) continue;
        if (!leases.TryClaim($"clips:{matchId}", agent)) continue;
        await clips.SavePlanAsync(plan, ct);
        var (myName, myChampion) = await CameraTargetAsync(matchId);
        return Results.Ok(new
        {
            Kind = "clips",
            plan.MatchId, plan.GameVersion, plan.DurationSec,
            ReplayUrl = $"/api/matches/{plan.MatchId}/replay",
            MyName = myName,
            MyChampion = myChampion,
            plan.Windows,
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
            ReplayUrl = $"/api/matches/{matchId}/replay",
            MyName = myName,
            MyChampion = myChampion,
            Windows = new[] { new ClipWindow(0, 0, (int)match.DurationSec, "full", []) },
        });
    }

    return Results.NoContent();
});

app.MapPut("/api/render/{matchId}/full", async (string matchId, HttpRequest request, FullGameService full, CancellationToken ct) =>
{
    if (full.VideoTargetPath(matchId) is not { } target) return Results.NotFound();

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

app.MapPut("/api/render/{matchId}/clips/{index:int}", async (string matchId, int index, HttpRequest request, ClipService clips, CancellationToken ct) =>
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

app.MapPost("/api/render/{matchId}/complete", (string matchId, RenderLeaseService leases, ClipService clips, FullGameService full, string kind = "clips") =>
{
    if (kind is "full") full.CompleteRequest(matchId);
    else clips.ClearFailed(matchId);
    leases.Release($"{kind}:{matchId}");
    return Results.Ok();
});

app.MapPost("/api/render/{matchId}/fail", async (string matchId, HttpRequest request, RenderLeaseService leases, ClipService clips, FullGameService full, string kind = "clips", CancellationToken ct = default) =>
{
    using var reader = new StreamReader(request.Body);
    var error = await reader.ReadToEndAsync(ct);
    error = error is { Length: > 0 } ? error.Trim() : "unknown";
    if (kind is "full") full.MarkFailed(matchId, error);
    else await clips.MarkFailedAsync(matchId, error, ct);
    leases.Release($"{kind}:{matchId}");
    return Results.Ok();
});

// Clears the failed marker so the agent picks the match up again.
app.MapPost("/api/render/{matchId}/retry", (string matchId, ClipService clips, FullGameService full, string kind = "clips") =>
{
    if (kind is "full") full.Request(matchId);
    else clips.ClearFailed(matchId);
    return Results.Ok();
});

app.MapGet("/api/matches/{id}", async (string id, LeagueDbContext db, ReplayArchiveService replays, CancellationToken ct) =>
{
    var match = await db.Matches.AsNoTracking()
        .Include(m => m.Participants.OrderBy(p => p.ParticipantId))
        .Include(m => m.DeathEvents.OrderBy(d => d.TimeSec)).ThenInclude(d => d.DamageInstances)
        .Include(m => m.ObjectiveEvents.OrderBy(o => o.TimeSec))
        .Include(m => m.ItemEvents.OrderBy(i => i.TimeSec))
        .FirstOrDefaultAsync(m => m.Id == id, ct);
    if (match is null) return Results.NotFound();

    var champByPid = match.Participants.ToDictionary(p => p.ParticipantId, p => p.Champion);
    object TeamObjectives(bool mine) => new
    {
        Towers = match.ObjectiveEvents.Count(o => o.Kind == "TOWER" && o.ByMyTeam == mine),
        Inhibitors = match.ObjectiveEvents.Count(o => o.Kind == "INHIBITOR" && o.ByMyTeam == mine),
        Dragons = match.ObjectiveEvents.Count(o => o.Kind == "DRAGON" && o.ByMyTeam == mine),
        Barons = match.ObjectiveEvents.Count(o => o.Kind == "BARON" && o.ByMyTeam == mine),
        Heralds = match.ObjectiveEvents.Count(o => o.Kind == "HERALD" && o.ByMyTeam == mine),
        Grubs = match.ObjectiveEvents.Count(o => o.Kind == "GRUBS" && o.ByMyTeam == mine),
        Atakhan = match.ObjectiveEvents.Count(o => o.Kind == "ATAKHAN" && o.ByMyTeam == mine),
    };

    return Results.Ok(new
    {
        Summary = MatchListItem(match, hasReplay: replays.PathFor(match.Id) is not null),
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
            p.Tier, p.Division, p.Lp, p.SeasonWins, p.SeasonLosses, p.RankValue, p.RankQueue,
            RankLabel = p.Tier is null ? null : $"{p.Tier} {p.Division} {p.Lp} LP",
            WinratePct = p is { SeasonWins: int w, SeasonLosses: int l } && w + l > 0 ? Math.Round(100.0 * w / (w + l), 1) : (double?)null,
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
app.MapGet("/api/analytics/summary", async (LeagueDbContext db, int lastN = 20, CancellationToken ct = default) =>
    Results.Ok(await Reports.AnalyticsSummaryAsync(db, lastN, ct)));

// Ladder percentiles (Challenges-V1) - how the player ranks vs everyone, the
// external benchmark the wins-vs-losses analysis can't provide.
app.MapGet("/api/challenges/percentiles", async (ChallengesBenchmarkService svc, CancellationToken ct) =>
    await svc.GetAsync(ct) is { } result ? Results.Ok(result) : Results.NoContent());

// The dashboard aggregate: coach-style stats over recent ranked games.
// lastGames takes precedence over days; neither = whole history.
app.MapGet("/api/stats", async (LeagueDbContext db, int? days, int? lastGames, CancellationToken ct) =>
    Results.Ok(await Reports.StatsAsync(db, days, lastGames, ct)));
app.MapPost("/api/ranks/backfill", (IServiceScopeFactory scopeFactory, JobStatusService jobs, int days = 7) =>
{
    if (!jobs.TryStart("rank-backfill")) return Results.Conflict(jobs.Snapshot());
    _ = Task.Run(async () =>
    {
        using var scope = scopeFactory.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<HistorySyncService>().BackfillRanksAsync(days, CancellationToken.None);
        }
        catch
        {
            // Already logged and surfaced via job status.
        }
    });
    return Results.Accepted("/api/jobs/status", jobs.Snapshot());
});

app.MapPost("/api/analytics/reprocess", (IServiceScopeFactory scopeFactory, JobStatusService jobs) =>
{
    if (!jobs.TryStart("reprocess")) return Results.Conflict(jobs.Snapshot());
    _ = Task.Run(async () =>
    {
        using var scope = scopeFactory.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<AnalyticsReprocessService>().ReprocessAsync(CancellationToken.None);
        }
        catch
        {
            // Already logged and surfaced via job status.
        }
    });
    return Results.Accepted("/api/jobs/status", jobs.Snapshot());
});

// --- LP -------------------------------------------------------------------------

app.MapGet("/api/lp/history", async (LeagueDbContext db, string? queue, CancellationToken ct) =>
{
    var query = db.LpSnapshots.AsNoTracking();
    if (queue is not null) query = query.Where(s => s.Queue == queue);
    var rows = await query.OrderBy(s => s.TimestampUtc).ToListAsync(ct);
    return Results.Ok(rows.Select(s => new
    {
        s.TimestampUtc, s.Queue, s.Tier, s.Division, s.Lp, s.Wins, s.Losses, s.RankValue,
        Label = $"{s.Tier} {s.Division} {s.Lp} LP",
    }));
});

app.MapGet("/api/lp/per-game", async (LeagueDbContext db, CancellationToken ct) =>
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
        m.LpBefore, m.LpAfter, m.LpChange,
    }));
});

// --- Background jobs: history backfill + import of the PowerShell exports --------

app.MapPost("/api/sync/history", (IServiceScopeFactory scopeFactory, JobStatusService jobs,
    int rankedTarget = 0, int maxMatches = 0, bool timeline = true, bool ranks = true) =>
{
    if (!jobs.TryStart("history-sync")) return Results.Conflict(jobs.Snapshot());
    _ = Task.Run(async () =>
    {
        using var scope = scopeFactory.CreateScope();
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
    return Results.Accepted("/api/jobs/status", jobs.Snapshot());
});

app.MapPost("/api/import", (string path, IServiceScopeFactory scopeFactory, JobStatusService jobs) =>
{
    if (!Directory.Exists(path)) return Results.BadRequest(new { error = $"Folder not found: {path}" });
    if (!jobs.TryStart("import")) return Results.Conflict(jobs.Snapshot());
    _ = Task.Run(async () =>
    {
        using var scope = scopeFactory.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<ImportService>().ImportFolderAsync(path, CancellationToken.None);
        }
        catch
        {
            // Already logged and surfaced via job status.
        }
    });
    return Results.Accepted("/api/jobs/status", jobs.Snapshot());
});

app.MapGet("/api/jobs/status", (JobStatusService jobs) => Results.Ok(jobs.Snapshot()));

// --- Exports (PowerShell-tooling-compatible CSV shapes + an everything-bundle) --

app.MapGet("/api/export/matches.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("matches-summary.csv", await Reports.MatchesCsvAsync(db, ct)));

app.MapGet("/api/export/deaths.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("deaths.csv", await Reports.DeathsCsvAsync(db, ct)));

app.MapGet("/api/export/ranks.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("ranks.csv", await Reports.RanksCsvAsync(db, ct)));

app.MapGet("/api/export/lp-history.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("lp-history.csv", await Reports.LpHistoryCsvAsync(db, ct)));

app.MapGet("/api/export/challenges.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("challenges.csv", await Reports.ChallengesCsvAsync(db, ct)));

app.MapGet("/api/export/lane-checkpoints.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("lane-checkpoints.csv", await Reports.LaneCheckpointsCsvAsync(db, ct)));

app.MapGet("/api/export/objectives.csv", async (LeagueDbContext db, CancellationToken ct) =>
    CsvFile("objectives.csv", await Reports.ObjectivesCsvAsync(db, ct)));

// Everything in one download: every CSV the screens are built from, plus the
// dashboard aggregate over all games as machine-readable JSON.
app.MapGet("/api/export/all.zip", async (LeagueDbContext db, LpService lp, TrackedPlayerService player, CancellationToken ct) =>
{
    var summary = new
    {
        player.RiotId,
        ExportedAtUtc = DateTime.UtcNow,
        Matches = await db.Matches.CountAsync(ct),
        Deaths = await db.Deaths.CountAsync(ct),
        Patches = await Reports.PatchesAsync(db, ct),
        Ranks = new[] { await lp.GetLatestAsync("Solo/Duo", ct), await lp.GetLatestAsync("Flex", ct) }
            .Where(s => s is not null)
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
    var dashboard = await Reports.StatsAsync(db, days: null, lastGames: null, ct);

    using var ms = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        async Task AddAsync(string name, string content)
        {
            await using var entry = zip.CreateEntry(name).Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
        }
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        await AddAsync("matches-summary.csv", await Reports.MatchesCsvAsync(db, ct));
        await AddAsync("challenges.csv", await Reports.ChallengesCsvAsync(db, ct));
        await AddAsync("lane-checkpoints.csv", await Reports.LaneCheckpointsCsvAsync(db, ct));
        await AddAsync("ranks.csv", await Reports.RanksCsvAsync(db, ct));
        await AddAsync("deaths.csv", await Reports.DeathsCsvAsync(db, ct));
        await AddAsync("objectives.csv", await Reports.ObjectivesCsvAsync(db, ct));
        await AddAsync("lp-history.csv", await Reports.LpHistoryCsvAsync(db, ct));
        await AddAsync("dashboard.json", System.Text.Json.JsonSerializer.Serialize(dashboard, jsonOpts));
        await AddAsync("summary.json", System.Text.Json.JsonSerializer.Serialize(summary, jsonOpts));
    }
    return Results.File(ms.ToArray(), "application/zip", $"leaguetracker-export-{DateTime.Now:yyyyMMdd-HHmm}.zip");
});
app.MapFallbackToFile("index.html");

app.Run();

static object MatchListItem(Match m, string? items = null, int? summoner1Id = null, int? summoner2Id = null, bool hasReplay = false) => new
{
    Items = items,
    Summoner1Id = summoner1Id,
    Summoner2Id = summoner2Id,
    HasReplay = hasReplay,
    m.Id, m.QueueId, m.QueueName, m.IsRanked, m.GameMode,
    Date = m.GameCreationUtc, m.GameEndUtc,
    DurationMin = Math.Round(m.DurationSec / 60, 1),
    m.Champion, m.Position, m.Win, m.Kills, m.Deaths, m.Assists,
    Kda = m.Deaths == 0 ? (m.Kills + m.Assists > 0 ? "Perfect" : "0") : Math.Round((m.Kills + m.Assists) / (double)m.Deaths, 2).ToString(),
    m.Cs, m.Gold, m.DamageToChampions, m.VisionScore, m.ChampLevel, m.HasTimeline,
    AvgAllyRank = m.AvgAllyRankValue is { } ally ? RankMath.ToLabel(ally) : null,
    AvgEnemyRank = m.AvgEnemyRankValue is { } enemy ? RankMath.ToLabel(enemy) : null,
    RankGapLp = m is { AvgAllyRankValue: { } a, AvgEnemyRankValue: { } e } ? (int?)Math.Round(e - a) : null,
    m.AllyRanksKnown, m.EnemyRanksKnown, m.RanksAtGameTime,
    m.LpChange, m.LpBefore, m.LpAfter,
    m.TimeInEnemyHalfPct, m.AvgNearestAllyDist,
    m.SkillshotsHit, m.SkillshotsDodged,
    m.OpponentChampion, m.EnemyJungler, m.AllyJungler, m.CsAt10, m.LaneGoldDiff10, m.KillParticipation, m.SoloKills,
    IsRemake = m.DurationSec < 300,
};

static IResult CsvFile(string fileName, string csv) =>
    Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);