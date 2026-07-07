using System.Text;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// CSV/report builders shared by the individual export endpoints and the
/// everything-bundle zip. Shapes stay compatible with the PowerShell tooling.
public static class Reports
{
    public static async Task<string> MatchesCsvAsync(LeagueDbContext db, CancellationToken ct)
    {
        var matches = await db.Matches.AsNoTracking().OrderByDescending(m => m.GameEndUtc).ToListAsync(ct);
        return Csv(
            ["MatchId", "Date", "Ranked", "Queue", "QueueId", "GameMode", "DurationMin", "Champion", "Position", "Win",
             "Kills", "Deaths", "Assists", "KDA", "CS", "Gold", "DmgToChamps", "VisionScore", "Level",
             "AvgAllyRank", "AvgEnemyRank", "RankGapLP", "AllyRanksIn", "EnemyRanksIn", "LPChange",
             "SkillshotsHit", "SkillshotsDodged", "TimeInEnemyHalfPct", "AvgNearestAllyDist"],
            matches.Select(m => new[]
            {
                m.Id, m.GameCreationUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), m.IsRanked.ToString(), m.QueueName,
                m.QueueId.ToString(), m.GameMode, Math.Round(m.DurationSec / 60, 1).ToString(), m.Champion, m.Position,
                m.Win.ToString(), m.Kills.ToString(), m.Deaths.ToString(), m.Assists.ToString(),
                m.Deaths == 0 ? "Perfect" : Math.Round((m.Kills + m.Assists) / (double)m.Deaths, 2).ToString(),
                m.Cs.ToString(), m.Gold.ToString(), m.DamageToChampions.ToString(), m.VisionScore.ToString(), m.ChampLevel.ToString(),
                m.AvgAllyRankValue is { } ally ? RankMath.ToLabel(ally) : "",
                m.AvgEnemyRankValue is { } enemy ? RankMath.ToLabel(enemy) : "",
                m is { AvgAllyRankValue: { } a, AvgEnemyRankValue: { } e } ? Math.Round(e - a).ToString() : "",
                $"{m.AllyRanksKnown}/5", $"{m.EnemyRanksKnown}/5", m.LpChange?.ToString() ?? "",
                m.SkillshotsHit?.ToString() ?? "", m.SkillshotsDodged?.ToString() ?? "",
                m.TimeInEnemyHalfPct?.ToString() ?? "", m.AvgNearestAllyDist?.ToString() ?? "",
            }));
    }

    public static async Task<string> DeathsCsvAsync(LeagueDbContext db, CancellationToken ct)
    {
        var rows = await db.Deaths.AsNoTracking()
            .Join(db.Matches.AsNoTracking(), d => d.MatchId, m => m.Id, (d, m) => new { d, m })
            .OrderByDescending(x => x.m.GameEndUtc).ThenBy(x => x.d.TimeSec)
            .ToListAsync(ct);
        return Csv(
            ["MatchId", "Date", "Champion", "Position", "GameTime", "TimeSec", "X", "Y", "KilledBy", "AssistedBy",
             "Assists", "DamageFrom", "EnemiesOnYou", "Bounty", "Shutdown", "MyLevel", "MyTotalGold", "MyCS",
             "EnemiesNearDeath", "AlliesNearDeath", "NearestAllyDist", "TotalDamageReceived", "TopSource", "TopSourceShare",
             "SecondsAfterObjective", "ObjectiveBefore"],
            rows.Select(x => new[]
            {
                x.d.MatchId, x.m.GameCreationUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), x.m.Champion, x.m.Position,
                $"{x.d.TimeSec / 60:00}:{x.d.TimeSec % 60:00}", x.d.TimeSec.ToString(), x.d.X.ToString(), x.d.Y.ToString(),
                x.d.KilledBy, x.d.AssistedBy,
                (x.d.AssistedBy.Length > 0 ? x.d.AssistedBy.Split(", ").Length : 0).ToString(),
                x.d.DamageFrom, x.d.EnemiesOnYou.ToString(), x.d.Bounty.ToString(), x.d.Shutdown.ToString(),
                x.d.MyLevel?.ToString() ?? "", x.d.MyTotalGold?.ToString() ?? "", x.d.MyCs?.ToString() ?? "",
                x.d.EnemiesNearDeath?.ToString() ?? "", x.d.AlliesNearDeath?.ToString() ?? "", x.d.NearestAllyDist?.ToString() ?? "",
                x.d.TotalDamageReceived?.ToString() ?? "", x.d.TopSource ?? "", x.d.TopSourceShare?.ToString() ?? "",
                x.d.SecondsAfterObjective?.ToString() ?? "", x.d.ObjectiveBefore ?? "",
            }));
    }

    public static async Task<string> RanksCsvAsync(LeagueDbContext db, CancellationToken ct)
    {
        var rows = await db.Participants.AsNoTracking()
            .Where(p => p.Tier != null)
            .Join(db.Matches.AsNoTracking(), p => p.MatchId, m => m.Id, (p, m) => new { p, m })
            .OrderByDescending(x => x.m.GameEndUtc).ThenBy(x => x.p.ParticipantId)
            .ToListAsync(ct);
        return Csv(
            ["MatchId", "Date", "Queue", "Team", "Position", "RiotId", "Champion", "Win",
             "Tier", "Division", "LP", "SeasonWins", "SeasonLosses", "WinratePct", "RankValue", "RankQueue"],
            rows.Select(x => new[]
            {
                x.p.MatchId, x.m.GameCreationUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), x.m.QueueName,
                x.p.IsMe ? "Me" : x.p.IsAlly ? "Ally" : "Enemy", x.p.Position, x.p.RiotId, x.p.Champion, x.p.Win.ToString(),
                x.p.Tier ?? "", x.p.Division ?? "", x.p.Lp?.ToString() ?? "",
                x.p.SeasonWins?.ToString() ?? "", x.p.SeasonLosses?.ToString() ?? "",
                x.p is { SeasonWins: int w, SeasonLosses: int l } && w + l > 0 ? Math.Round(100.0 * w / (w + l), 1).ToString() : "",
                x.p.RankValue?.ToString() ?? "", x.p.RankQueue ?? "",
            }));
    }

    public static async Task<string> LpHistoryCsvAsync(LeagueDbContext db, CancellationToken ct)
    {
        var rows = await db.LpSnapshots.AsNoTracking().OrderBy(s => s.TimestampUtc).ToListAsync(ct);
        return Csv(
            ["Timestamp", "Queue", "Tier", "Division", "LP", "Wins", "Losses", "RankValue"],
            rows.Select(s => new[]
            {
                s.TimestampUtc.ToString("o"), s.Queue, s.Tier, s.Division,
                s.Lp.ToString(), s.Wins.ToString(), s.Losses.ToString(), s.RankValue.ToString(),
            }));
    }

    /// Collapse-focused death analytics over the recent ranked games with
    /// timelines - deliberately centred on collapse count and contest quality.
    public static async Task<object> AnalyticsSummaryAsync(LeagueDbContext db, int lastN, CancellationToken ct)
    {
        var matches = await db.Matches.AsNoTracking()
            .Where(m => m.IsRanked && m.HasTimeline)
            .OrderByDescending(m => m.GameEndUtc)
            .Take(Math.Clamp(lastN, 1, 500))
            .Include(m => m.DeathEvents)
            .ToListAsync(ct);
        var deaths = matches.SelectMany(m => m.DeathEvents).ToList();
        var analysed = deaths.Where(d => d.EnemiesNearDeath is not null).ToList();

        return new
        {
            Games = matches.Count,
            TotalDeaths = deaths.Count,
            DeathsPerGame = matches.Count > 0 ? Math.Round((double)deaths.Count / matches.Count, 2) : 0,
            // The active target: walked into 3+ enemies who were actually there.
            CollapseDeaths = analysed.Count(d => d.EnemiesNearDeath >= 3),
            IsolatedDeaths = analysed.Count(d => d.AlliesNearDeath == 0),
            PostObjectiveDeaths = deaths.Count(d => d.SecondsAfterObjective is not null),
            BurstDeaths = deaths.Count(d => d.TopSourceShare >= 0.7),
            AvgEnemiesNearDeath = analysed is { Count: > 0 } ? Math.Round(analysed.Average(d => d.EnemiesNearDeath!.Value), 2) : (double?)null,
            AvgNearestAllyDistAtDeath = analysed is { Count: > 0 } ? (int?)analysed.Where(d => d.NearestAllyDist is not null).Select(d => d.NearestAllyDist!.Value).DefaultIfEmpty(0).Average() : null,
            AvgTimeInEnemyHalfPct = matches.Where(m => m.TimeInEnemyHalfPct is not null).Select(m => m.TimeInEnemyHalfPct!.Value).DefaultIfEmpty().Average(),
            AvgNearestAllyDistOverall = matches.Where(m => m.AvgNearestAllyDist is not null).Select(m => (double)m.AvgNearestAllyDist!.Value).DefaultIfEmpty().Average(),
            AvgSkillshotsHit = Math.Round(matches.Where(m => m.SkillshotsHit is not null).Select(m => (double)m.SkillshotsHit!.Value).DefaultIfEmpty().Average(), 1),
            AvgSkillshotsDodged = Math.Round(matches.Where(m => m.SkillshotsDodged is not null).Select(m => (double)m.SkillshotsDodged!.Value).DefaultIfEmpty().Average(), 1),
        };
    }

    /// Distinct game patches (major.minor) across stored matches, oldest first.
    public static async Task<List<string>> PatchesAsync(LeagueDbContext db, CancellationToken ct)
    {
        var versions = await db.Matches.AsNoTracking().Select(m => m.GameVersion).Distinct().ToListAsync(ct);
        return versions
            .Select(v => string.Join('.', v.Split('.').Take(2)))
            .Where(v => v.Length > 0)
            .Distinct()
            .OrderBy(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
            .ToList();
    }

    private static string Csv(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        AppendLine(sb, headers);
        foreach (var row in rows) AppendLine(sb, row);
        return sb.ToString();

        static void AppendLine(StringBuilder sb, string[] fields) =>
            sb.AppendJoin(',', fields.Select(f => $"\"{f.Replace("\"", "\"\"")}\"")).AppendLine();
    }
}
