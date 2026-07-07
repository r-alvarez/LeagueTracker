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
builder.Services.AddHttpClient<RiotApiClient>(c => c.Timeout = TimeSpan.FromSeconds(60))
    .AddHttpMessageHandler<RiotRateLimitHandler>();

builder.Services.AddSingleton<DataPaths>();
builder.Services.AddScoped<RankLookupService>();
builder.Services.AddScoped<LpService>();
builder.Services.AddScoped<MatchIngestService>();
builder.Services.AddScoped<TrackedPlayerService>();
builder.Services.AddScoped<HistorySyncService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<AnalyticsReprocessService>();
builder.Services.AddHostedService<MatchPollerService>();

// Vite dev server origin; irrelevant in production where the SPA is served by this host.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<LeagueDbContext>().Database.EnsureCreated();
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// --- Status ---------------------------------------------------------------------

app.MapGet("/api/status", async (LeagueDbContext db, LpService lp, TrackedPlayerService player, IRiotKeyProvider keys, JobStatusService jobs, CancellationToken ct) =>
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

// --- Matches --------------------------------------------------------------------

app.MapGet("/api/matches", async (LeagueDbContext db, int page = 1, int pageSize = 20, bool? ranked = null, string? champion = null, CancellationToken ct = default) =>
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

    return Results.Ok(new
    {
        total,
        items = items.Select(m => MatchListItem(m,
            loadouts.TryGetValue(m.Id, out var l) ? l.Items : null,
            loadouts.TryGetValue(m.Id, out var l1) ? l1.Summoner1Id : null,
            loadouts.TryGetValue(m.Id, out var l2) ? l2.Summoner2Id : null)),
    });
});

app.MapGet("/api/matches/{id}", async (string id, LeagueDbContext db, CancellationToken ct) =>
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
        Summary = MatchListItem(match),
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

// The dashboard aggregate: coach-style stats over recent ranked games.
// lastGames takes precedence over days; neither = whole history.
app.MapGet("/api/stats", async (LeagueDbContext db, int? days, int? lastGames, CancellationToken ct) =>
    Results.Ok(await Reports.StatsAsync(db, days, lastGames, ct)));
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

// Everything in one download: the four CSVs plus a machine-readable summary.
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
        Analytics = await Reports.AnalyticsSummaryAsync(db, 50, ct),
    };

    using var ms = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        async Task AddAsync(string name, string content)
        {
            await using var entry = zip.CreateEntry(name).Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
        }
        await AddAsync("matches-summary.csv", await Reports.MatchesCsvAsync(db, ct));
        await AddAsync("deaths.csv", await Reports.DeathsCsvAsync(db, ct));
        await AddAsync("ranks.csv", await Reports.RanksCsvAsync(db, ct));
        await AddAsync("lp-history.csv", await Reports.LpHistoryCsvAsync(db, ct));
        await AddAsync("summary.json", System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    return Results.File(ms.ToArray(), "application/zip", $"leaguetracker-export-{DateTime.Now:yyyyMMdd-HHmm}.zip");
});
app.MapFallbackToFile("index.html");

app.Run();

static object MatchListItem(Match m, string? items = null, int? summoner1Id = null, int? summoner2Id = null) => new
{
    Items = items,
    Summoner1Id = summoner1Id,
    Summoner2Id = summoner2Id,
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
    m.OpponentChampion, m.EnemyJungler, m.CsAt10, m.LaneGoldDiff10, m.KillParticipation, m.SoloKills,
};

static IResult CsvFile(string fileName, string csv) =>
    Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);