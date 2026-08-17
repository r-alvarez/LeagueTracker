using System.Text.Json;
using LeagueTracker.Api.Data;

namespace LeagueTracker.Api.Services;

/// Per-game metric rows shared by the Lens and the Fundamentals ladder: one
/// metric-key -> value dictionary per match, plus the self-percentile math both
/// features score with (a score of N = the recent window's mean sits at the
/// player's own Nth percentile).
public static class MatchMetricRows
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record FightDto(string Kind, string Result, bool Participated, int GoldSwing, bool ConvertedObjective);

    public static Dictionary<string, double> ComputeRow(Match m, int collapses, int deathsAfterObjective)
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
                    if (decisive > 0) values[$"{key}Winrate"] = 100.0 * mine.Count(f => f.Result is "won") / decisive;
                    values[$"{key}Gold"] = mine.Sum(f => f.GoldSwing);
                }
                var won = fights.Where(f => f.Participated && f.Result is "won").ToList();
                if (won is { Count: > 0 }) values["fightConversion"] = 100.0 * won.Count(f => f.ConvertedObjective) / won.Count;
            }
            catch { /* old rows without fights just skip these metrics */ }
        }

        return values;
    }

    /// Selected numeric fields of the per-game challenges block, keyed by Riot's
    /// field name - absent keys (old/corrupt games) are simply skipped.
    public static void AddChallengeValues(Dictionary<string, double> row, Match m, IReadOnlyDictionary<string, string> keyMap)
    {
        if (m.ChallengesJson is not { Length: > 0 }) return;
        try
        {
            using var doc = JsonDocument.Parse(m.ChallengesJson);
            foreach (var (riotKey, rowKey) in keyMap)
            {
                if (doc.RootElement.TryGetProperty(riotKey, out var v)
                    && v.ValueKind is JsonValueKind.Number && v.TryGetDouble(out var d))
                {
                    row[rowKey] = d;
                }
            }
        }
        catch { /* malformed block - skip */ }
    }

    public static double? Mean(List<Dictionary<string, double>> set, string key)
    {
        var vals = set.Where(r => r.ContainsKey(key)).Select(r => r[key]).ToList();
        return vals is { Count: > 0 } ? vals.Average() : null;
    }

    /// Where the recent window's mean for this key sits among the means of every
    /// equally long window in the player's EARLIER history (0-100, ties split):
    /// like against like, and the window never ranks against itself - both of
    /// which pushed every score towards 50 as the window grew. A history too
    /// short for five such windows falls back to the baseline's single-game
    /// values: noisier, but a young account still gets a number. Null when
    /// either side lacks the data.
    public static double? Percentile(
        List<Dictionary<string, double>> baseline, List<Dictionary<string, double>> recent, string key, bool higherIsBetter)
    {
        if (Mean(recent, key) is not { } target) return null;
        var vals = baseline.Where(r => r.ContainsKey(key)).Select(r => r[key]).ToList();
        if (vals.Count < 5) return null;
        var windowSize = recent.Count(r => r.ContainsKey(key));
        var reference = vals.Count >= windowSize + 4 ? RollingMeans(vals, windowSize) : vals;
        var below = reference.Count(v => v < target) + reference.Count(v => v == target) * 0.5;
        var pct = below / reference.Count;
        return Math.Round(100 * (higherIsBetter ? pct : 1 - pct));
    }

    private static List<double> RollingMeans(List<double> vals, int window)
    {
        var means = new List<double>(vals.Count - window + 1);
        var sum = vals.Take(window).Sum();
        means.Add(sum / window);
        for (var i = window; i < vals.Count; i++)
        {
            sum += vals[i] - vals[i - window];
            means.Add(sum / window);
        }
        return means;
    }
}
