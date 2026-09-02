using System.Security.Cryptography;
using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

public sealed record ReferencePoint(string Id, string Phase, string Text, RuleSpec Rule);

public sealed record Gameplan(string Champion, List<ReferencePoint> Points, DateTime UpdatedUtc);

public sealed record ReferencePointInput(string? Id, string? Phase, string? Text, RuleSpec? Rule);

public sealed record PointEvaluation(string Id, string Phase, string Text, RuleSpec Rule, RuleResult Result);

public sealed record MatchGameplan(
    string MatchId, string Champion, bool HasPlan, List<PointEvaluation> Points, Dictionary<string, int> Summary);

public sealed record GameplanBundlePlan(string? Champion, List<ReferencePointInput>? Points);

public sealed record GameplanBundle(List<GameplanBundlePlan>? Plans, DateTime? ExportedUtc = null);

public sealed record GameplanImportResult(string Champion, int Points, string? Error);

// Plans are irreplaceable, so they are files and the db is never written;
// rules run at read time so editing a plan never needs a reprocess. Every
// point carries a rule: what the tracker cannot score is not in the plan.
public sealed class GameplanService(LeagueDbContext db, DataPaths paths)
{
    public const int MaxPoints = 24;
    public const int MaxTextLength = 200;
    private const int MaxAdherenceGames = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string Root => Path.Combine(paths.DataDir, "gameplans");

    // Riot champion names are plain ASCII; anything else never becomes a file name.
    private static string? ChampionKey(string? champion) =>
        champion is { Length: > 0 and <= 40 } && champion.All(char.IsAsciiLetterOrDigit) ? champion.ToLowerInvariant() : null;

    // --- plans -------------------------------------------------------------------------------

    public List<object> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, "*.json")
            .Select(ReadPlan)
            .Where(p => p is not null)
            .OrderBy(p => p!.Champion)
            .Select(p => (object)new { p!.Champion, Points = p.Points.Count, p.UpdatedUtc })
            .ToList();
    }

    public Gameplan? Get(string champion) =>
        ChampionKey(champion) is { } key ? ReadPlan(Path.Combine(Root, $"{key}.json")) : null;

    public (Gameplan? Plan, string? Error) Save(string champion, List<ReferencePointInput>? points)
    {
        if (ChampionKey(champion) is not { } key) return (null, "That is not a champion name.");
        points ??= [];
        if (points.Count > MaxPoints) return (null, $"A plan holds at most {MaxPoints} reference points.");

        var existingIds = Get(champion)?.Points.Select(p => p.Id).ToHashSet() ?? [];
        HashSet<string> seen = [];
        List<ReferencePoint> cleaned = [];
        foreach (var input in points)
        {
            var text = input.Text?.Trim() ?? "";
            if (text.Length == 0) return (null, "Every reference point needs a sentence.");
            if (text.Length > MaxTextLength) return (null, $"Keep each reference point under {MaxTextLength} characters.");
            var phase = input.Phase?.Trim().ToLowerInvariant() ?? "";
            if (!GameplanRules.Phases.Contains(phase)) return (null, $"Phase must be one of {string.Join(", ", GameplanRules.Phases)}.");
            if (GameplanRules.Normalize(input.Rule) is not { } rule) return (null, $"\"{text}\" needs a rule - the tracker only keeps points it can score.");
            // Ids survive edits so history stays comparable; unknown ids are not trusted.
            var id = input.Id is { Length: > 0 } && existingIds.Contains(input.Id) && seen.Add(input.Id) ? input.Id : NewId(seen);
            cleaned.Add(new ReferencePoint(id, phase, text, rule));
        }

        var plan = new Gameplan(champion, cleaned, DateTime.UtcNow);
        WriteAtomic(Path.Combine(Root, $"{key}.json"), plan);
        return (plan, null);
    }

    public bool Delete(string champion)
    {
        if (ChampionKey(champion) is not { } key) return false;
        var path = Path.Combine(Root, $"{key}.json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // --- transfer ----------------------------------------------------------------------------

    public GameplanBundle Export()
    {
        var plans = Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*.json").Select(ReadPlan).Where(p => p is not null).OrderBy(p => p!.Champion)
                .Select(p => new GameplanBundlePlan(p!.Champion,
                    p.Points.Select(x => new ReferencePointInput(x.Id, x.Phase, x.Text, x.Rule)).ToList()))
                .ToList()
            : [];
        return new GameplanBundle(plans, DateTime.UtcNow);
    }

    // Each plan lands or fails on its own; a typo in one champion's sheet must
    // not hold the others back.
    public List<GameplanImportResult> Import(GameplanBundle? bundle)
    {
        List<GameplanImportResult> results = [];
        foreach (var plan in bundle?.Plans ?? [])
        {
            var champion = plan.Champion?.Trim() ?? "";
            var (saved, error) = Save(champion, plan.Points);
            results.Add(new GameplanImportResult(champion, saved?.Points.Count ?? 0, error));
        }
        return results;
    }

    public string? ImportFile(string path)
    {
        try
        {
            var bundle = JsonSerializer.Deserialize<GameplanBundle>(File.ReadAllText(path), Json);
            var results = Import(bundle);
            var failed = results.Where(r => r.Error is not null).ToList();
            return failed is [] ? null : string.Join("; ", failed.Select(r => $"{r.Champion}: {r.Error}"));
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }
    }

    // --- evaluation --------------------------------------------------------------------------

    public async Task<MatchGameplan?> EvaluateAsync(string matchId, CancellationToken ct)
    {
        var contexts = await LoadContextsAsync([matchId], ct);
        return contexts.TryGetValue(matchId, out var ctx) ? Evaluate(ctx, Get(ctx.Match.Champion)) : null;
    }

    public async Task<object?> AdherenceAsync(string champion, int last, CancellationToken ct)
    {
        if (Get(champion) is not { } plan) return null;
        var ids = await db.Matches.AsNoTracking()
            .Where(m => m.Champion == plan.Champion && m.HasTimeline && m.DurationSec >= 300)
            .OrderByDescending(m => m.GameEndUtc)
            .Take(Math.Clamp(last, 1, MaxAdherenceGames))
            .Select(m => m.Id)
            .ToArrayAsync(ct);

        var contexts = await LoadContextsAsync(ids, ct);
        var games = ids.Where(contexts.ContainsKey)
            .Select(id => (Id: id, contexts[id].Match.Win, Result: Evaluate(contexts[id], plan)))
            .ToList();

        var points = plan.Points.Select(p =>
        {
            var statuses = games.Select(g => (g.Id, g.Result.Points.First(e => e.Id == p.Id).Result.Status, g.Win)).ToList();
            return new
            {
                p.Id, p.Phase, p.Text, p.Rule,
                Met = statuses.Count(s => s.Status is GameplanRules.Met),
                Missed = statuses.Count(s => s.Status is GameplanRules.Missed),
                Na = statuses.Count(s => s.Status is GameplanRules.NotApplicable),
                Pending = statuses.Count(s => s.Status is GameplanRules.Pending),
                // Outcome-conditioned: context for the player, never a verdict.
                WinsWhenMet = statuses.Count(s => s.Status is GameplanRules.Met && s.Win),
                WinsWhenMissed = statuses.Count(s => s.Status is GameplanRules.Missed && s.Win),
                Recent = statuses.Select(s => new { MatchId = s.Id, s.Status }),
            };
        });

        return new
        {
            plan.Champion,
            Games = games.Select(g => new { g.Id, g.Win, contexts[g.Id].Match.GameEndUtc, g.Result.Summary }),
            Points = points,
        };
    }

    internal static MatchGameplan Evaluate(RuleContext ctx, Gameplan? plan)
    {
        List<PointEvaluation> points = [];
        foreach (var point in plan?.Points ?? [])
        {
            // A phase the game never reached is nobody's miss.
            var result = ctx.DurationSec < GameplanRules.PhaseStartSec(point.Phase)
                ? new RuleResult(GameplanRules.NotApplicable, $"Game ended before the {point.Phase} game.")
                : GameplanRules.Evaluate(point.Rule, ctx);
            points.Add(new PointEvaluation(point.Id, point.Phase, point.Text, point.Rule, result));
        }

        var summary = points.GroupBy(p => p.Result.Status).ToDictionary(g => g.Key, g => g.Count());
        return new MatchGameplan(ctx.Match.Id, ctx.Match.Champion, plan is not null, points, summary);
    }

    private async Task<Dictionary<string, RuleContext>> LoadContextsAsync(string[] ids, CancellationToken ct)
    {
        var matches = await db.Matches.AsNoTracking().Where(m => ids.Contains(m.Id) && m.HasTimeline).ToListAsync(ct);
        var participants = (await db.Participants.AsNoTracking().Where(p => ids.Contains(p.MatchId)).ToListAsync(ct))
            .GroupBy(p => p.MatchId).ToDictionary(g => g.Key, g => g.ToList());
        var kills = (await db.KillEvents.AsNoTracking().Where(k => ids.Contains(k.MatchId)).ToListAsync(ct))
            .GroupBy(k => k.MatchId).ToDictionary(g => g.Key, g => g.OrderBy(k => k.TimeSec).ToList());
        var objectives = (await db.ObjectiveEvents.AsNoTracking().Where(o => ids.Contains(o.MatchId)).ToListAsync(ct))
            .GroupBy(o => o.MatchId).ToDictionary(g => g.Key, g => g.ToList());
        var positions = (await db.PositionSamples.AsNoTracking().Where(p => ids.Contains(p.MatchId)).ToListAsync(ct))
            .GroupBy(p => p.MatchId).ToDictionary(g => g.Key, g => g.ToList());
        var items = (await db.ItemEvents.AsNoTracking().Where(i => ids.Contains(i.MatchId)).ToListAsync(ct))
            .GroupBy(i => i.MatchId).ToDictionary(g => g.Key, g => g.ToList());
        var deaths = (await db.Deaths.AsNoTracking().Where(d => ids.Contains(d.MatchId)).ToListAsync(ct))
            .GroupBy(d => d.MatchId).ToDictionary(g => g.Key, g => g.OrderBy(d => d.TimeSec).ToList());

        Dictionary<string, RuleContext> result = [];
        foreach (var m in matches)
        {
            var parts = participants.GetValueOrDefault(m.Id) ?? [];
            if (parts.FirstOrDefault(p => p.IsMe) is not { } me) continue;
            var fights = m.FightsJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<List<TimelineAnalyzer.Fight>>(m.FightsJson, Json) ?? []
                : [];
            var levelSecs = m.LevelSecs.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            result[m.Id] = new RuleContext(m, me, parts,
                kills.GetValueOrDefault(m.Id) ?? [], objectives.GetValueOrDefault(m.Id) ?? [],
                positions.GetValueOrDefault(m.Id) ?? [], items.GetValueOrDefault(m.Id) ?? [], fights, levelSecs,
                deaths.GetValueOrDefault(m.Id) ?? []);
        }
        return result;
    }

    // --- files -------------------------------------------------------------------------------

    // Points whose rule this build cannot evaluate (a kind removed or never
    // known) are dropped on read rather than shown unscored.
    private static Gameplan? ReadPlan(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<RawPlan>(File.ReadAllText(path), Json);
            if (raw is null) return null;
            var points = raw.Points
                .Select(p => GameplanRules.Normalize(p.Rule) is { } rule ? new ReferencePoint(p.Id, p.Phase, p.Text, rule) : null)
                .Where(p => p is not null)
                .Cast<ReferencePoint>()
                .ToList();
            return new Gameplan(raw.Champion, points, raw.UpdatedUtc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RawPoint(string Id, string Phase, string Text, RuleSpec? Rule);
    private sealed record RawPlan(string Champion, List<RawPoint> Points, DateTime UpdatedUtc);

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static string NewId(HashSet<string> taken)
    {
        while (true)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            if (taken.Add(id)) return id;
        }
    }
}
