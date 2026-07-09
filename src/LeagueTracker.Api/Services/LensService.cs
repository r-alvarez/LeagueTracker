using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// The Lens: dpm-style coaching scores, computed honestly against the player's
/// OWN history rather than an invented cohort. A score of 73 means "the recent
/// window averages at your personal 73rd percentile"; the Challenges card
/// stays the external ladder anchor.
public sealed class LensService(LeagueDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record FightDto(string Kind, string Result, bool Participated, int GoldSwing, bool ConvertedObjective);

    private sealed record Spec(string Key, string Label, string Desc, string Category, string? Sub, bool HigherIsBetter, int Decimals, string Unit = "");

    private static readonly Spec[] Specs =
    [
        // Fighting - general
        new("kp", "Kill participation", "Share of your team's kills you took part in", "fighting", "general", true, 0, "%"),
        new("dpm", "Damage / min", "Damage to champions per minute", "fighting", "general", true, 0),
        new("dpmEarly", "DMG/m early", "Damage per minute, first 10 minutes", "fighting", "general", true, 0),
        new("dpmMid", "DMG/m mid", "Damage per minute, minutes 10-20", "fighting", "general", true, 0),
        new("dpmLate", "DMG/m late", "Damage per minute after minute 20", "fighting", "general", true, 0),
        new("dmgTaken", "DMG taken / min", "Damage received per minute", "fighting", "general", false, 0),
        new("soloKills", "Solo kills", "Unassisted kills per game", "fighting", "general", true, 1),
        new("multiKills", "Multikills", "Triples and better per game", "fighting", "general", true, 2),
        // Fighting - duels / skirmishes / teamfights (from detected fights)
        new("duelCount", "Duels / game", "1v1 fights you took part in", "fighting", "duels", true, 1),
        new("duelWinrate", "Duel winrate", "1v1 fights your side won", "fighting", "duels", true, 0, "%"),
        new("duelGold", "Duel gold swing", "Net team gold from your duels per game", "fighting", "duels", true, 0, "g"),
        new("skirmishCount", "Skirmishes / game", "Small fights (2-5 involved) you took part in", "fighting", "skirmishes", true, 1),
        new("skirmishWinrate", "Skirmish winrate", "Small fights your side won", "fighting", "skirmishes", true, 0, "%"),
        new("skirmishGold", "Skirmish gold swing", "Net team gold from your skirmishes per game", "fighting", "skirmishes", true, 0, "g"),
        new("teamfightCount", "Teamfights / game", "3+ a side fights you took part in", "fighting", "teamfights", true, 1),
        new("teamfightWinrate", "Teamfight winrate", "Teamfights your side won", "fighting", "teamfights", true, 0, "%"),
        new("teamfightGold", "Teamfight gold swing", "Net team gold from your teamfights per game", "fighting", "teamfights", true, 0, "g"),
        new("fightConversion", "Fight conversion", "Won fights followed by an objective within 45s", "fighting", "teamfights", true, 0, "%"),
        // Fighting - mechanics
        new("ssHit", "Skillshots hit", "Landed skillshots per game", "fighting", "mechanics", true, 0),
        new("ssDodged", "Skillshots dodged", "Dodged skillshots per game", "fighting", "mechanics", true, 0),
        // Laning
        new("g10", "Gold diff @10", "Gold lead vs your lane opponent at 10:00", "laning", null, true, 0, "g"),
        new("xp10", "XP diff @10", "XP lead vs your lane opponent at 10:00", "laning", null, true, 0),
        new("cs10", "CS @10", "Creep score at 10:00", "laning", null, true, 0),
        new("g15", "Gold diff @15", "Gold lead vs your lane opponent at 15:00", "laning", null, true, 0, "g"),
        new("firstTo2", "First to level 2", "How often you hit level 2 first", "laning", null, true, 0, "%"),
        new("lvl6Lead", "Level 6 lead", "Seconds ahead of lane to level 6 (+ = first)", "laning", null, true, 0, "s"),
        // Objectives
        new("objPresence", "Objective presence", "Friendly epic objectives you were near", "objectives", null, true, 0, "%"),
        new("deathsAfterObj", "Overstay deaths", "Deaths shortly after your team took an objective", "objectives", null, false, 2),
        // Vision
        new("visPerMin", "Vision / min", "Vision score per minute", "vision", null, true, 2),
        new("wards", "Wards placed", "Wards placed per game", "vision", null, true, 1),
        new("controlWards", "Control wards", "Control wards per game", "vision", null, true, 1),
        new("wardsFirst10", "Early wards", "Wards placed in the first 10 minutes", "vision", null, true, 1),
        new("wardsKilled", "Wards killed", "Enemy wards cleared per game", "vision", null, true, 1),
        // Survivability
        new("deaths", "Deaths / game", "Fewer is better", "survivability", null, false, 1),
        new("followIns", "Follow-in deaths", "Dying right after a teammate fell", "survivability", null, false, 2),
        new("collapses", "Collapse deaths", "Deaths with 3+ enemies converged", "survivability", null, false, 2),
        new("timeDeadPct", "Time dead", "Share of the game spent on the gray screen", "survivability", null, false, 1, "%"),
        new("longestAlive", "Longest life", "Longest stretch alive, minutes", "survivability", null, true, 1, "m"),
    ];

    private static readonly (string Key, string Label)[] Categories =
    [
        ("fighting", "Fighting"), ("laning", "Laning phase"), ("objectives", "Objectives"),
        ("vision", "Vision"), ("survivability", "Survivability"),
    ];

    private static readonly (string Key, string Label, string Desc)[] FightingSubs =
    [
        ("general", "General", "Damage, participation and kill quality"),
        ("duels", "Duels", "1v1 fights"),
        ("skirmishes", "Skirmishes", "2v2 up to small-group fights"),
        ("teamfights", "Teamfights", "3+ players on both sides"),
        ("mechanics", "Mechanics", "Micro skills"),
    ];

    private static readonly HashSet<string> Roles = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];

    public async Task<object?> GetAsync(int window, string? role, CancellationToken ct)
    {
        var query = db.Matches.AsNoTracking()
            .Where(m => m.IsRanked && m.DurationSec >= 300);
        if (role is { Length: > 0 } && Roles.Contains(role.ToUpperInvariant()))
        {
            var normalized = role.ToUpperInvariant();
            query = query.Where(m => m.Position == normalized);
        }

        var matches = await query
            .OrderByDescending(m => m.GameEndUtc)
            .ToListAsync(ct);
        if (matches.Count < 8) return null;

        var deathAgg = (await db.Deaths.AsNoTracking()
                .GroupBy(d => d.MatchId)
                .Select(g => new
                {
                    MatchId = g.Key,
                    Collapses = g.Count(d => d.EnemiesNearDeath >= 3),
                    AfterObjective = g.Count(d => d.SecondsAfterObjective != null),
                })
                .ToListAsync(ct))
            .ToDictionary(x => x.MatchId);

        var rows = matches.Select(m => ComputeRow(m,
            deathAgg.TryGetValue(m.Id, out var da) ? da.Collapses : 0,
            deathAgg.TryGetValue(m.Id, out var db2) ? db2.AfterObjective : 0)).ToList();

        window = Math.Clamp(window, 5, rows.Count);
        var recent = rows.Take(window).ToList();
        var baseline = rows.Skip(window).ToList();
        var hasBaseline = baseline.Count >= 5;

        // Header context: winrate over the window and the era's signature champion
        // (most played) for the NEW vs OLD portrait pair.
        var recentMatches = matches.Take(window).ToList();
        var baselineMatches = matches.Skip(window).ToList();
        var winrate = Math.Round(100.0 * recentMatches.Count(m => m.Win) / recentMatches.Count);
        static string? TopChampion(List<Match> set) => set
            .GroupBy(m => m.Champion).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

        static double? Mean(List<Dictionary<string, double>> set, string key)
        {
            var vals = set.Where(r => r.ContainsKey(key)).Select(r => r[key]).ToList();
            return vals is { Count: > 0 } ? vals.Average() : null;
        }

        double? Percentile(string key, bool higherIsBetter)
        {
            var recentMean = Mean(recent, key);
            if (recentMean is not { } target) return null;
            var all = rows.Where(r => r.ContainsKey(key)).Select(r => r[key]).ToList();
            if (all.Count < 5) return null;
            var below = all.Count(v => v < target) + all.Count(v => v == target) * 0.5;
            var pct = below / all.Count;
            return Math.Round(100 * (higherIsBetter ? pct : 1 - pct));
        }

        object Tile(Spec s) => new
        {
            s.Key, s.Label, s.Desc, s.Unit, s.Decimals, s.HigherIsBetter,
            Value = Mean(recent, s.Key) is { } v ? Math.Round(v, s.Decimals) : (double?)null,
            Old = hasBaseline && Mean(baseline, s.Key) is { } o ? Math.Round(o, s.Decimals) : (double?)null,
        };

        double? ScoreOf(IEnumerable<Spec> specs)
        {
            var pcts = specs.Select(s => Percentile(s.Key, s.HigherIsBetter)).OfType<double>().ToList();
            return pcts is { Count: > 0 } ? Math.Round(pcts.Average()) : null;
        }

        var categories = Categories.Select(cat =>
        {
            var catSpecs = Specs.Where(s => s.Category == cat.Key).ToList();
            var subs = cat.Key == "fighting"
                ? FightingSubs.Select(sub =>
                {
                    var subSpecs = catSpecs.Where(s => s.Sub == sub.Key).ToList();
                    return new
                    {
                        sub.Key, sub.Label, sub.Desc,
                        Score = ScoreOf(subSpecs),
                        Tiles = subSpecs.Select(Tile),
                    };
                }).ToList()
                : null;
            return new
            {
                cat.Key, cat.Label,
                Score = ScoreOf(catSpecs),
                Subs = (object?)subs,
                Tiles = subs is null ? catSpecs.Select(Tile) : null,
            };
        });

        return new
        {
            Games = rows.Count,
            Window = window,
            HasBaseline = hasBaseline,
            Winrate = winrate,
            TopChampion = TopChampion(recentMatches),
            TopChampionOld = hasBaseline ? TopChampion(baselineMatches) : null,
            Categories = categories,
        };
    }

    private Dictionary<string, double> ComputeRow(Match m, int collapses, int deathsAfterObjective)
    {
        var minutes = Math.Max(1, m.DurationSec / 60.0);
        var values = new Dictionary<string, double>
        {
            ["dpm"] = m.DamageToChampions / minutes,
            ["soloKills"] = m.SoloKills,
            ["multiKills"] = m.TripleKills + m.QuadraKills + m.PentaKills,
            ["deaths"] = m.Deaths,
            ["followIns"] = m.FollowInDeaths,
            ["collapses"] = collapses,
            ["deathsAfterObj"] = deathsAfterObjective,
            ["timeDeadPct"] = 100.0 * m.TotalTimeSpentDead / Math.Max(1, m.DurationSec),
            ["longestAlive"] = m.LongestTimeSpentLiving / 60.0,
            ["visPerMin"] = m.VisionScore / minutes,
            ["wards"] = m.WardsPlaced,
            ["controlWards"] = m.ControlWards,
            ["wardsFirst10"] = m.WardsFirst10,
            ["wardsKilled"] = m.WardsKilled,
        };

        void Opt(string key, double? v) { if (v is { } x && !double.IsNaN(x)) values[key] = x; }
        Opt("cs10", m.CsAt10);
        Opt("kp", m.KillParticipation is { } kpv ? kpv * 100 : null);
        Opt("dpmEarly", m.DpmEarly);
        Opt("dpmMid", m.DpmMid);
        Opt("dpmLate", m.DpmLate);
        Opt("dmgTaken", m.DamageTakenPerMin);
        Opt("ssHit", m.SkillshotsHit);
        Opt("ssDodged", m.SkillshotsDodged);
        Opt("g10", m.LaneGoldDiff10);
        Opt("xp10", m.LaneXpDiff10);
        Opt("g15", m.LaneGoldDiff15);
        Opt("firstTo2", m.FirstToLevel2 is { } f2 ? (f2 ? 100 : 0) : null);
        Opt("lvl6Lead", m.Level6LeadSec);
        Opt("objPresence", m.FriendlyEpicObjectives > 0 ? 100.0 * m.ObjectivesPresentFor / m.FriendlyEpicObjectives : null);

        if (m.FightsJson is { Length: > 0 })
        {
            try
            {
                var fights = JsonSerializer.Deserialize<List<FightDto>>(m.FightsJson, Json) ?? [];
                foreach (var (kind, key) in new[] { ("duel", "duel"), ("skirmish", "skirmish"), ("teamfight", "teamfight") })
                {
                    var mine = fights.Where(f => f.Participated && f.Kind == kind).ToList();
                    values[$"{key}Count"] = mine.Count;
                    var decisive = mine.Count(f => f.Result is "won" or "lost");
                    if (decisive > 0) values[$"{key}Winrate"] = 100.0 * mine.Count(f => f.Result == "won") / decisive;
                    values[$"{key}Gold"] = mine.Sum(f => f.GoldSwing);
                }
                var won = fights.Where(f => f.Participated && f.Result == "won").ToList();
                if (won is { Count: > 0 }) values["fightConversion"] = 100.0 * won.Count(f => f.ConvertedObjective) / won.Count;
            }
            catch { /* old rows without fights just skip these metrics */ }
        }

        return values;
    }
}
