using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// The Fundamentals ladder: the coaching-curriculum skills (macro, trading,
/// jungle tracking, ...) pinned to the rank tier where each one typically gates
/// progress. Every skill is evidenced two ways and only two ways:
///  - self-percentile over the player's own games (the Lens's honest math), and
///  - Riot's OWN Challenges levels as the ladder anchor where a mapping exists.
/// Deliberately no invented overall rating - per-skill evidence only, so it
/// stays a training map rather than an alternative ranking system.
public sealed class FundamentalsService(LeagueDbContext db, ChallengesBenchmarkService challenges)
{
    private sealed record Spec(string Key, string Label, string Desc, bool HigherIsBetter, int Decimals, string Unit = "");

    private sealed record Area(
        string Key, string Label, string Tier, string Desc, string Measured, long[] ChallengeIds, Spec[] Metrics);

    /// A team-gold lead/deficit beyond this at 15:00 counts as ahead/behind.
    private const int GoldStateThreshold = 1000;
    /// Deaths before this second count as laning phase for the gank signal.
    private const int LanePhaseEndSec = 840;

    // Riot's per-game challenges-block fields mined into metric rows.
    private static readonly Dictionary<string, string> ChallengeBlockKeys = new()
    {
        ["turretPlatesTaken"] = "plates",
        ["killsOnOtherLanesEarlyJungleAsLaner"] = "roamKills",
        ["visionScoreAdvantageLaneOpponent"] = "visionAdvLane",
        ["maxCsAdvantageOnLaneOpponent"] = "maxCsAdv",
        ["maxLevelLeadLaneOpponent"] = "maxLevelLead",
    };

    // The curriculum: each skill sits at the tier where it usually starts
    // gating games (the coaching-ladder picture), NOT at the player's level.
    private static readonly Area[] Areas =
    [
        new("macro", "Macro", "GOLD",
            "Playing the map, not just the lane: rotations, objectives, spending gold.",
            "Objective presence, turret plates, early roam kills, unspent gold and time dead from your games; Riot's gold/objective challenges as the ladder anchor.",
            [202305, 302106, 203304, 201001],
            [
                new("objPresence", "Objective presence", "Friendly epic objectives you were near", true, 0, "%"),
                new("plates", "Turret plates", "Plates you claimed before 14:00", true, 1),
                new("roamKills", "Early roam kills", "Kills in OTHER lanes early as a laner", true, 2),
                new("unspentGold", "Gold left unspent", "Average gold carried without spending", false, 0, "g"),
                new("timeDeadPct", "Time dead", "Share of the game on the gray screen", false, 1, "%"),
            ]),
        new("info", "Information gathering", "GOLD",
            "Taking information off the map: denying vision, tracking who's missing, telling your team.",
            "Wards cleared, vision lead over your lane opponent and missing-pings from your games; Riot's ward-hunting challenges as the ladder anchor.",
            [402401, 204201, 204203, 202202],
            [
                new("wardsKilled", "Wards killed", "Enemy wards cleared per game", true, 1),
                new("visionAdvLane", "Vision lead vs lane", "Vision score minus your lane opponent's", true, 1),
                new("missingPings", "Missing pings", "Enemy-missing pings you sent per game", true, 1),
            ]),
        new("matchup", "Matchup understanding", "PLATINUM",
            "Knowing what your lane can and can't do: when to press, when to concede cs, when you scale.",
            "Lane leads at 10, biggest cs/level leads over your opponent and solo kills from your games; Riot's lane-dominance challenges as the ladder anchor.",
            [202103, 202102, 202101, 202105, 203301],
            [
                new("g10", "Gold diff @10", "Gold lead vs your lane opponent at 10:00", true, 0, "g"),
                new("xp10", "XP diff @10", "XP lead vs your lane opponent at 10:00", true, 0),
                new("maxCsAdv", "Max CS lead", "Biggest CS lead held over your laner", true, 0, "cs"),
                new("maxLevelLead", "Max level lead", "Biggest level lead over your laner", true, 1),
                new("soloKills", "Solo kills", "Unassisted kills per game", true, 1),
            ]),
        new("wincon", "Win condition assessment", "PLATINUM",
            "Reading the game state: closing when ahead, not forcing when behind, converting fights into the map.",
            "Conversion when ahead/behind at 15:00 (team gold), fight-to-objective conversion and overstay deaths from your games; Riot's comeback/closing challenges as the ladder anchor.",
            [301203, 301202, 301201, 302106],
            [
                new("winAhead15", "Won when ahead @15", "Games won when your team led by 1k+ gold at 15:00", true, 0, "%"),
                new("winBehind15", "Won when behind @15", "Games rescued from 1k+ down at 15:00", true, 0, "%"),
                new("fightConversion", "Fight conversion", "Won fights followed by an objective within 45s", true, 0, "%"),
                new("deathsAfterObj", "Overstay deaths", "Deaths shortly after your team took an objective", false, 2),
            ]),
        new("trading", "Trading", "EMERALD",
            "Winning the short exchanges: hitting and dodging what matters, racing the level spikes.",
            "Early damage, skillshots landed/dodged and the level-2/level-6 races from your games; Riot's skillshot challenges as the ladder anchor.",
            [203101, 101201, 101202, 103102, 103205],
            [
                new("dpmEarly", "DMG/m early", "Damage per minute, first 10 minutes", true, 0),
                new("ssHit", "Skillshots hit", "Landed skillshots per game", true, 0),
                new("ssDodged", "Skillshots dodged", "Dodged skillshots per game", true, 0),
                new("firstTo2", "First to level 2", "How often you hit level 2 first", true, 0, "%"),
                new("lvl6Lead", "Level 6 lead", "Seconds ahead of lane to level 6 (+ = first)", true, 0, "s"),
            ]),
        new("teamfight", "Teamfighting", "EMERALD",
            "The 5v5s: taking the right fights, surviving them, and not feeding the collapse.",
            "Detected-teamfight winrate and gold swings, multikills, collapse and follow-in deaths from your games; Riot's teamplay challenges as the ladder anchor.",
            [302402, 302302, 302401, 203203, 203201, 203106, 402105],
            [
                new("teamfightWinrate", "Teamfight winrate", "Teamfights your side won", true, 0, "%"),
                new("teamfightCount", "Teamfights / game", "3+ a side fights you took part in", true, 1),
                new("teamfightGold", "Teamfight gold swing", "Net team gold from your teamfights per game", true, 0, "g"),
                new("multiKills", "Multikills", "Triples and better per game", true, 2),
                new("collapses", "Collapse deaths", "Deaths with 3+ enemies converged", false, 2),
                new("followIns", "Follow-in deaths", "Dying right after a teammate fell", false, 2),
            ]),
        new("jungletrack", "Jungle tracking", "DIAMOND",
            "Knowing where the enemy jungler is without seeing them - and not dying when you're wrong.",
            "Laning-phase deaths with the enemy jungler converged (interpolated positions) and early wards from your games. No honest Riot challenge maps here, so there is no ladder anchor - your own trend is the evidence.",
            [],
            [
                new("gankDeathsPre14", "Gank deaths", "Laning-phase deaths with the enemy jungler on you", false, 2),
                new("lanePhaseDeaths", "Deaths before 14:00", "All laning-phase deaths, for context", false, 2),
                new("wardsFirst10", "Early wards", "Wards placed in the first 10 minutes", true, 1),
            ]),
        new("warding", "Warding & laning", "DIAMOND",
            "Vision as a laning weapon: warding early, warding deep, keeping your wards alive.",
            "Vision score pace, first-ward timing and control wards from your games; Riot's warding challenges as the ladder anchor.",
            [402402, 402403, 202104, 302105, 204202],
            [
                new("visPerMin", "Vision / min", "Vision score per minute", true, 2),
                new("wardsFirst10", "Early wards", "Wards placed in the first 10 minutes", true, 1),
                new("firstWardSec", "First ward", "Seconds until your first ward", false, 0, "s"),
                new("firstControlWardSec", "First control ward", "Seconds until your first control ward", false, 0, "s"),
                new("controlWards", "Control wards", "Control wards per game", true, 1),
                new("wards", "Wards placed", "Wards placed per game", true, 1),
            ]),
    ];

    private static readonly HashSet<string> Roles = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];

    /// Window semantics mirror the Lens: last N games, or everything within
    /// `days` when given; role optionally scopes to one position.
    public async Task<object?> GetAsync(int window, int? days, string? role, CancellationToken ct)
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
                    GankDeaths = g.Count(d => d.EnemyJunglerNear == true && d.TimeSec < LanePhaseEndSec),
                    LaneDeaths = g.Count(d => d.TimeSec < LanePhaseEndSec),
                })
                .ToListAsync(ct))
            .ToDictionary(x => x.MatchId);

        var matchIds = matches.Select(m => m.Id).ToList();
        var missingPings = (await db.Participants.AsNoTracking()
                .Where(p => p.IsMe && matchIds.Contains(p.MatchId))
                .Select(p => new { p.MatchId, p.PingsJson })
                .ToListAsync(ct))
            .ToDictionary(p => p.MatchId, p => PingCount(p.PingsJson, "Missing"));

        var rows = matches.Select(m =>
        {
            var agg = deathAgg.GetValueOrDefault(m.Id);
            var row = MatchMetricRows.ComputeRow(m, agg?.Collapses ?? 0, agg?.AfterObjective ?? 0);
            MatchMetricRows.AddChallengeValues(row, m, ChallengeBlockKeys);

            void Opt(string key, double? v) { if (v is { } x) row[key] = x; }
            Opt("unspentGold", m.AvgUnspentGold);
            Opt("firstWardSec", m.FirstWardSec);
            Opt("firstControlWardSec", m.FirstControlWardSec);
            if (m.HasTimeline)
            {
                row["gankDeathsPre14"] = agg?.GankDeaths ?? 0;
                row["lanePhaseDeaths"] = agg?.LaneDeaths ?? 0;
            }
            if (missingPings.TryGetValue(m.Id, out var pings)) row["missingPings"] = pings;
            // Conditional keys: only games that WERE ahead/behind carry the metric,
            // so the rate reads "of the games in that state, how many converted".
            if (m.TeamGoldDiff15 is { } tg15)
            {
                if (tg15 >= GoldStateThreshold) row["winAhead15"] = m.Win ? 100 : 0;
                else if (tg15 <= -GoldStateThreshold) row["winBehind15"] = m.Win ? 100 : 0;
            }
            return row;
        }).ToList();

        if (days is { } d)
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(d, 1, 365));
            window = matches.Count(m => m.GameEndUtc >= cutoff);
            if (window < 3) return null;
        }
        window = Math.Clamp(window, 3, rows.Count);
        var recent = rows.Take(window).ToList();
        var baseline = rows.Skip(window).ToList();
        var hasBaseline = baseline.Count >= 5;

        var recentMatches = matches.Take(window).ToList();
        var winrate = Math.Round(100.0 * recentMatches.Count(m => m.Win) / recentMatches.Count);

        var standings = await challenges.GetStandingsAsync(ct);
        var standingById = (standings?.Rows ?? []).ToDictionary(s => s.Id);

        object Tile(Spec s) => new
        {
            s.Key, s.Label, s.Desc, s.Unit, s.Decimals, s.HigherIsBetter,
            Value = MatchMetricRows.Mean(recent, s.Key) is { } v ? Math.Round(v, s.Decimals) : (double?)null,
            Old = hasBaseline && MatchMetricRows.Mean(baseline, s.Key) is { } o ? Math.Round(o, s.Decimals) : (double?)null,
            Series = recent.AsEnumerable().Reverse()
                .Select(r => r.TryGetValue(s.Key, out var g) ? Math.Round(g, s.Decimals) : (double?)null)
                .ToList(),
        };

        var areas = Areas.Select(a =>
        {
            var pcts = a.Metrics
                .Select(s => MatchMetricRows.Percentile(rows, recent, s.Key, s.HigherIsBetter))
                .OfType<double>().ToList();
            var mapped = a.ChallengeIds
                .Select(id => standingById.GetValueOrDefault(id))
                .OfType<ChallengeStanding>()
                .OrderByDescending(s => s.LevelRank)
                .ToList();

            // The anchor level is the MEDIAN of the mapped challenges (lower
            // middle on ties - conservative), so one outlier grind doesn't set it.
            object? ladder = null;
            if (mapped is { Count: > 0 })
            {
                var median = mapped.OrderBy(s => s.LevelRank).ElementAt((mapped.Count - 1) / 2);
                var percentiles = mapped.Where(s => s.Percentile is not null).Select(s => s.Percentile!.Value).OrderBy(p => p).ToList();
                ladder = new
                {
                    median.Level,
                    median.LevelRank,
                    TopShare = percentiles is { Count: > 0 } ? percentiles[(percentiles.Count - 1) / 2] : (double?)null,
                    Challenges = mapped,
                };
            }

            return new
            {
                a.Key, a.Label, a.Tier, a.Desc, a.Measured,
                Score = pcts is { Count: > 0 } ? Math.Round(pcts.Average()) : (double?)null,
                Tiles = a.Metrics.Select(Tile).ToList(),
                Ladder = ladder,
            };
        }).ToList();

        return new
        {
            Games = rows.Count,
            Window = window,
            HasBaseline = hasBaseline,
            Winrate = winrate,
            ChallengesAsOfUtc = standings?.AsOfUtc,
            Areas = areas,
        };
    }

    private static int PingCount(string pingsJson, string kind)
    {
        if (pingsJson is not { Length: > 0 }) return 0;
        try
        {
            using var doc = JsonDocument.Parse(pingsJson);
            return doc.RootElement.TryGetProperty(kind, out var v) ? v.GetInt32() : 0;
        }
        catch { return 0; }
    }
}
