using System.Security.Cryptography;
using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

public sealed record ReferencePoint(string Id, string Phase, string Text, RuleSpec? Rule);

public sealed record Gameplan(string Champion, List<ReferencePoint> Points, DateTime UpdatedUtc);

public sealed record ReferencePointInput(string? Id, string? Phase, string? Text, RuleSpec? Rule);

public sealed record PointRating(string Status, string? Note, DateTime RatedUtc);

public sealed record MatchChecks(string MatchId, Dictionary<string, PointRating> Ratings);

public sealed record PointEvaluation(
    string Id, string Phase, string Text, RuleSpec? Rule, RuleResult? Auto, PointRating? Self, string Status);

public sealed record MatchGameplan(
    string MatchId, string Champion, bool HasPlan, List<PointEvaluation> Points, Dictionary<string, int> Summary);

// Plans and ratings are irreplaceable, so they are files and the db is never
// written; rules run at read time so editing a plan never needs a reprocess.
public sealed class GameplanService(LeagueDbContext db, DataPaths paths)
{
    public const string Unrated = "unrated";
    public const int MaxPoints = 24;
    public const int MaxTextLength = 200;
    public const int MaxNoteLength = 500;
    private const int MaxAdherenceGames = 200;

    private static readonly string[] SelfStatuses = [GameplanRules.Met, GameplanRules.Missed, GameplanRules.NotApplicable];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string Root => Path.Combine(paths.DataDir, "gameplans");
    private string ChecksDir => Path.Combine(Root, "checks");

    // Riot champion names are plain ASCII; anything else never becomes a file name.
    private static string? ChampionKey(string? champion) =>
        champion is { Length: > 0 and <= 40 } && champion.All(char.IsAsciiLetterOrDigit) ? champion.ToLowerInvariant() : null;

    private static string? MatchKey(string matchId) =>
        matchId is { Length: > 0 and <= 40 } && matchId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_') ? matchId : null;

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
            // Ids survive edits so ratings stay attached; unknown ids are not trusted.
            var id = input.Id is { Length: > 0 } && existingIds.Contains(input.Id) && seen.Add(input.Id) ? input.Id : NewId(seen);
            cleaned.Add(new ReferencePoint(id, phase, text, GameplanRules.Normalize(input.Rule)));
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

    // --- the player's own ratings ----------------------------------------------------------------

    public MatchChecks? Checks(string matchId) =>
        MatchKey(matchId) is { } key ? Read<MatchChecks>(Path.Combine(ChecksDir, $"{key}.json")) : null;

    public string? Rate(string matchId, string pointId, string? status, string? note)
    {
        if (MatchKey(matchId) is not { } key) return "Unknown match.";
        if (pointId is not { Length: > 0 and <= 32 } || !pointId.All(char.IsAsciiLetterOrDigit)) return "Unknown reference point.";
        if (status is not null && !SelfStatuses.Contains(status)) return $"A rating is one of {string.Join(", ", SelfStatuses)}.";
        var trimmedNote = note?.Trim();
        if (trimmedNote is { Length: > MaxNoteLength }) return $"Keep the note under {MaxNoteLength} characters.";

        var checks = Checks(matchId) ?? new MatchChecks(matchId, []);
        if (status is null) checks.Ratings.Remove(pointId);
        else checks.Ratings[pointId] = new PointRating(status, trimmedNote is { Length: > 0 } ? trimmedNote : null, DateTime.UtcNow);

        var path = Path.Combine(ChecksDir, $"{key}.json");
        if (checks.Ratings is { Count: > 0 }) WriteAtomic(path, checks);
        else if (File.Exists(path)) File.Delete(path);
        return null;
    }

    // --- evaluation --------------------------------------------------------------------------

    public async Task<MatchGameplan?> EvaluateAsync(string matchId, CancellationToken ct)
    {
        var contexts = await LoadContextsAsync([matchId], ct);
        if (!contexts.TryGetValue(matchId, out var ctx)) return null;
        return Evaluate(ctx, Get(ctx.Match.Champion), Checks(matchId));
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
            .Select(id => (Id: id, Result: Evaluate(contexts[id], plan, Checks(id))))
            .ToList();

        var points = plan.Points.Select(p =>
        {
            var statuses = games.Select(g => (g.Id, g.Result.Points.First(e => e.Id == p.Id).Status, contexts[g.Id].Match.Win)).ToList();
            return new
            {
                p.Id, p.Phase, p.Text, p.Rule,
                Met = statuses.Count(s => s.Status is GameplanRules.Met),
                Missed = statuses.Count(s => s.Status is GameplanRules.Missed),
                Na = statuses.Count(s => s.Status is GameplanRules.NotApplicable),
                Pending = statuses.Count(s => s.Status is GameplanRules.Pending),
                Unrated = statuses.Count(s => s.Status is Unrated),
                // Outcome-conditioned: context for the player, never a verdict.
                WinsWhenMet = statuses.Count(s => s.Status is GameplanRules.Met && s.Win),
                WinsWhenMissed = statuses.Count(s => s.Status is GameplanRules.Missed && s.Win),
                Recent = statuses.Select(s => new { MatchId = s.Id, s.Status }),
            };
        });

        return new
        {
            plan.Champion,
            Games = games.Select(g => new { g.Id, contexts[g.Id].Match.Win, contexts[g.Id].Match.GameEndUtc, g.Result.Summary }),
            Points = points,
        };
    }

    internal static MatchGameplan Evaluate(RuleContext ctx, Gameplan? plan, MatchChecks? checks)
    {
        List<PointEvaluation> points = [];
        foreach (var point in plan?.Points ?? [])
        {
            // A phase the game never reached is nobody's miss, manual points included.
            var auto = ctx.DurationSec < GameplanRules.PhaseStartSec(point.Phase)
                ? new RuleResult(GameplanRules.NotApplicable, $"Game ended before the {point.Phase} game.")
                : point.Rule is not null ? GameplanRules.Evaluate(point.Rule, ctx) : null;
            var self = checks?.Ratings.GetValueOrDefault(point.Id);
            var status = self?.Status ?? auto?.Status ?? Unrated;
            points.Add(new PointEvaluation(point.Id, point.Phase, point.Text, point.Rule, auto, self, status));
        }

        var summary = points.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count());
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

    private static Gameplan? ReadPlan(string path)
    {
        var plan = Read<Gameplan>(path);
        return plan is null ? null : plan with { Points = plan.Points.Select(p => p with { Rule = GameplanRules.Normalize(p.Rule) }).ToList() };
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
