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
             "SecondsAfterObjective", "ObjectiveBefore", "Zone",
             "FollowTeammate", "FollowTeammateRole", "FollowSecondsAfter", "FollowDistance", "FollowAlliesDownBefore", "FollowPureLoss", "FollowTeamGoldDiff"],
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
                x.d.SecondsAfterObjective?.ToString() ?? "", x.d.ObjectiveBefore ?? "", x.d.Zone,
                x.d.FollowTeammate ?? "", x.d.FollowTeammateRole ?? "", x.d.FollowSecondsAfter?.ToString() ?? "",
                x.d.FollowDistance?.ToString() ?? "", x.d.FollowAlliesDownBefore?.ToString() ?? "",
                x.d.FollowPureLoss?.ToString() ?? "", x.d.FollowTeamGoldDiff?.ToString() ?? "",
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
            .Where(m => m.IsRanked && m.HasTimeline && m.DurationSec >= 300)
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

    /// The dashboard aggregate: coach-style season stats over a window of recent
    /// ranked games (lastGames takes precedence over days; both null = all).
    /// Definitions follow the League Coach engine so numbers match like-for-like.
    public static async Task<object> StatsAsync(LeagueDbContext db, int? days, int? lastGames, CancellationToken ct)
    {
        // Remakes (<5 min) carry no LP and no signal - they don't count as games here.
        var query = db.Matches.AsNoTracking()
            .Where(m => m.IsRanked && m.DurationSec >= 300)
            .OrderByDescending(m => m.GameEndUtc);
        List<Match> matches;
        if (lastGames is > 0)
        {
            matches = await query.Take(Math.Clamp(lastGames.Value, 1, 1000)).Include(m => m.DeathEvents).ToListAsync(ct);
        }
        else if (days is > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days.Value);
            matches = await query.Where(m => m.GameEndUtc >= cutoff).Include(m => m.DeathEvents).ToListAsync(ct);
        }
        else
        {
            matches = await query.Include(m => m.DeathEvents).ToListAsync(ct);
        }

        var chrono = matches.OrderBy(m => m.GameEndUtc).ToList();
        var wins = matches.Count(m => m.Win);
        var deaths = matches.SelectMany(m => m.DeathEvents).ToList();
        var withLane = matches.Where(m => m.LaneGoldDiff10 is not null).ToList();
        var followIns = deaths.Where(d => d.FollowTeammate is not null).ToList();

        static double Avg(IEnumerable<double> xs) { var l = xs.ToList(); return l is { Count: > 0 } ? l.Average() : 0; }
        double PerGame(int count) => matches.Count > 0 ? Math.Round(count / (double)matches.Count, 2) : 0;
        double DurMin(Match m) => Math.Max(1.0, m.DurationSec / 60.0);
        double Kda(Match m) => (m.Kills + m.Assists) / (double)Math.Max(1, m.Deaths);
        static object Bucket(List<Match> ms) => new
        {
            Games = ms.Count,
            Wins = ms.Count(x => x.Win),
            WinRate = ms is { Count: > 0 } ? Math.Round(ms.Count(x => x.Win) / (double)ms.Count, 3) : 0,
        };
        static List<object> Counted(IEnumerable<string> keys, int total) => keys
            .GroupBy(k => k)
            .Select(g => new { Key = g.Key, Count = g.Count(), Share = total > 0 ? Math.Round(g.Count() / (double)total, 3) : 0 })
            .OrderByDescending(c => c.Count).Take(8).Cast<object>().ToList();

        var ahead = withLane.Where(m => m.LaneGoldDiff10 >= 500).ToList();
        var behind = withLane.Where(m => m.LaneGoldDiff10 <= -500).ToList();
        var even = withLane.Where(m => m.LaneGoldDiff10 is > -500 and < 500).ToList();

        // Lead trajectory: does a 10-minute state survive to 20:00? Read from the
        // stored lane checkpoints (mid/late data exists only when the game got there).
        var checkpointJson = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        int? GoldAt(Match m, int minute)
        {
            if (m.LaneDiffsJson is not { Length: > 0 }) return null;
            try
            {
                return System.Text.Json.JsonSerializer
                    .Deserialize<List<TimelineAnalyzer.LaneDiffPoint>>(m.LaneDiffsJson, checkpointJson)?
                    .FirstOrDefault(c => c.Min == minute)?.Gold;
            }
            catch { return null; }
        }
        var aheadWith20 = ahead.Select(m => GoldAt(m, 20)).Where(g => g is not null).Select(g => g!.Value).ToList();
        var behindWith20 = behind.Select(m => GoldAt(m, 20)).Where(g => g is not null).Select(g => g!.Value).ToList();
        var leadsAt20 = matches
            .Select(m => new { m.Win, Gold = GoldAt(m, 20) })
            .Where(x => x.Gold >= 500)
            .ToList();
        var laneTrajectory = new
        {
            LeadsHeldAt20 = new { Held = aheadWith20.Count(g => g >= 500), Of = aheadWith20.Count },
            DeficitsRecoveredAt20 = new { Recovered = behindWith20.Count(g => g > -500), Of = behindWith20.Count },
            LeadsAt20Won = new { Won = leadsAt20.Count(x => x.Win), Of = leadsAt20.Count },
        };
        var at15 = new
        {
            Ahead = Bucket(matches.Where(m => m.LaneGoldDiff15 >= 500).ToList()),
            Even = Bucket(matches.Where(m => m.LaneGoldDiff15 is > -500 and < 500).ToList()),
            Behind = Bucket(matches.Where(m => m.LaneGoldDiff15 <= -500).ToList()),
        };

        List<object> SplitBy(Func<Match, string> key, bool withDetail = false) => matches
            .GroupBy(key).Where(g => g.Key is { Length: > 0 })
            .Select(g => new
            {
                Key = g.Key,
                Games = g.Count(),
                Wins = g.Count(m => m.Win),
                WinRate = Math.Round(g.Count(m => m.Win) / (double)g.Count(), 3),
                Kda = Math.Round(Avg(g.Select(Kda)), 2),
                Kp = Math.Round(Avg(g.Where(m => m.KillParticipation is not null).Select(m => m.KillParticipation!.Value)), 3),
                CsPerMin = Math.Round(Avg(g.Select(m => m.Cs / DurMin(m))), 2),
                Dpm = Math.Round(Avg(g.Select(m => m.DamageToChampions / DurMin(m))), 1),
                LaneGoldAt10 = g.Any(m => m.LaneGoldDiff10 is not null)
                    ? (int?)Math.Round(Avg(g.Where(m => m.LaneGoldDiff10 is not null).Select(m => (double)m.LaneGoldDiff10!)))
                    : null,
                DeathsPerGame = Math.Round(Avg(g.Select(m => (double)m.Deaths)), 2),
                // Drill-down extras: score-line averages and lane matchups.
                Detail = withDetail
                    ? (object?)new
                    {
                        AvgKills = Math.Round(Avg(g.Select(m => (double)m.Kills)), 1),
                        AvgDeaths = Math.Round(Avg(g.Select(m => (double)m.Deaths)), 1),
                        AvgAssists = Math.Round(Avg(g.Select(m => (double)m.Assists)), 1),
                        CsAt10 = Math.Round(Avg(g.Where(m => m.CsAt10 is not null).Select(m => (double)m.CsAt10!)), 1),
                        SoloKillsPerGame = Math.Round(Avg(g.Select(m => (double)m.SoloKills)), 2),
                        VisionPerMin = Math.Round(Avg(g.Select(m => m.VisionScore / DurMin(m))), 2),
                        SkillshotsDodgedPerGame = Math.Round(Avg(g.Where(m => m.SkillshotsDodged is not null).Select(m => (double)m.SkillshotsDodged!)), 1),
                        Matchups = g.Where(m => m.OpponentChampion is not null)
                            .GroupBy(m => m.OpponentChampion!)
                            .Where(x => x.Count() >= 2)
                            .Select(x => new
                            {
                                Opponent = x.Key,
                                Games = x.Count(),
                                WinRate = Math.Round(x.Count(m => m.Win) / (double)x.Count(), 3),
                                LaneGoldAt10 = x.Any(m => m.LaneGoldDiff10 is not null)
                                    ? (int?)Math.Round(Avg(x.Where(m => m.LaneGoldDiff10 is not null).Select(m => (double)m.LaneGoldDiff10!)))
                                    : null,
                                Kda = Math.Round(Avg(x.Select(Kda)), 2),
                            })
                            .OrderByDescending(x => x.Games).ThenBy(x => x.WinRate)
                            .Take(10).ToList(),
                    }
                    : null,
            })
            .OrderByDescending(s => s.Games).Cast<object>().ToList();

        // Rolling last-10 win rate + laning series, oldest first.
        var series = new List<object>();
        for (var i = 0; i < chrono.Count; i++)
        {
            var from = Math.Max(0, i - 9);
            var window = chrono.Skip(from).Take(i - from + 1).ToList();
            series.Add(new
            {
                chrono[i].Id,
                Date = chrono[i].GameCreationUtc,
                chrono[i].Win,
                N = i + 1,
                RollingWinRate10 = Math.Round(100.0 * window.Count(m => m.Win) / window.Count, 1),
                LaneGoldAt10 = chrono[i].LaneGoldDiff10,
                chrono[i].CsAt10,
            });
        }

        var lpDeltas = new List<object>();
        foreach (var queue in new[] { "Solo/Duo", "Flex" })
        {
            var rows = await db.LpSnapshots.AsNoTracking().Where(s => s.Queue == queue).OrderBy(s => s.TimestampUtc).ToListAsync(ct);
            if (rows is not { Count: > 0 }) continue;
            int? Delta(int overDays)
            {
                var cutoff = DateTime.UtcNow.AddDays(-overDays);
                var inWindow = rows.Where(r => r.TimestampUtc >= cutoff).ToList();
                return inWindow is { Count: > 1 } ? inWindow[^1].RankValue - inWindow[0].RankValue : null;
            }
            lpDeltas.Add(new { Queue = queue, Last7 = Delta(7), Last30 = Delta(30) });
        }

        var laneByRole = matches
            .Where(m => m.LaneGoldDiff10 is not null && m.Position is { Length: > 0 })
            .GroupBy(m => m.Position)
            .Select(g => new { Role = g.Key, Avg = (int)Math.Round(Avg(g.Select(m => (double)m.LaneGoldDiff10!))) })
            .OrderBy(r => r.Avg).ToList();

        var overall = new
        {
            Kda = Math.Round(Avg(matches.Select(Kda)), 2),
            Kp = Math.Round(Avg(matches.Where(m => m.KillParticipation is not null).Select(m => m.KillParticipation!.Value)), 3),
            Dpm = Math.Round(Avg(matches.Select(m => m.DamageToChampions / DurMin(m))), 0),
            Gpm = Math.Round(Avg(matches.Select(m => m.Gold / DurMin(m))), 0),
            CsPerMin = Math.Round(Avg(matches.Select(m => m.Cs / DurMin(m))), 2),
            CsAt10 = Math.Round(Avg(matches.Where(m => m.CsAt10 is not null).Select(m => (double)m.CsAt10!)), 1),
            LaneGoldAt10 = withLane is { Count: > 0 } ? (int?)Math.Round(Avg(withLane.Select(m => (double)m.LaneGoldDiff10!))) : null,
            LaneCsAt10 = matches.Any(m => m.LaneCsDiff10 is not null)
                ? (int?)Math.Round(Avg(matches.Where(m => m.LaneCsDiff10 is not null).Select(m => (double)m.LaneCsDiff10!)))
                : null,
            LaneGoldAt10ByRole = laneByRole,
            VisionPerMin = Math.Round(Avg(matches.Select(m => m.VisionScore / DurMin(m))), 2),
            ControlWardsPerGame = Math.Round(Avg(matches.Select(m => (double)m.ControlWards)), 1),
            DeathsPerGame = PerGame(deaths.Count),
            DeathsPre10 = PerGame(deaths.Count(d => d.TimeSec < 600)),
            Deaths10To20 = PerGame(deaths.Count(d => d.TimeSec is >= 600 and < 1200)),
            DeathsPost20 = PerGame(deaths.Count(d => d.TimeSec >= 1200)),
            SoloKillsPerGame = Math.Round(Avg(matches.Select(m => (double)m.SoloKills)), 2),
            DpmEarly = Math.Round(Avg(matches.Where(m => m.DpmEarly is not null).Select(m => m.DpmEarly!.Value)), 0),
            DpmMid = Math.Round(Avg(matches.Where(m => m.DpmMid is not null).Select(m => m.DpmMid!.Value)), 0),
            DpmLate = Math.Round(Avg(matches.Where(m => m.DpmLate is not null).Select(m => m.DpmLate!.Value)), 0),
            DamageTakenPerMin = Math.Round(Avg(matches.Where(m => m.DamageTakenPerMin is not null).Select(m => m.DamageTakenPerMin!.Value)), 0),
            Triples = matches.Sum(m => m.TripleKills),
            Quadras = matches.Sum(m => m.QuadraKills),
            Pentas = matches.Sum(m => m.PentaKills),
            SkillshotsHitPerGame = Math.Round(Avg(matches.Where(m => m.SkillshotsHit is not null).Select(m => (double)m.SkillshotsHit!)), 1),
            SkillshotsDodgedPerGame = Math.Round(Avg(matches.Where(m => m.SkillshotsDodged is not null).Select(m => (double)m.SkillshotsDodged!)), 1),
        };

        return new
        {
            Scope = new
            {
                Games = matches.Count,
                Wins = wins,
                Losses = matches.Count - wins,
                WinRate = matches.Count > 0 ? Math.Round(wins / (double)matches.Count, 3) : 0,
                DateFrom = chrono is { Count: > 0 } ? chrono[0].GameCreationUtc.ToLocalTime().ToString("yyyy-MM-dd") : null,
                DateTo = chrono is { Count: > 0 } ? chrono[^1].GameCreationUtc.ToLocalTime().ToString("yyyy-MM-dd") : null,
                Champions = matches.Select(m => m.Champion).Distinct().Count(),
            },
            Overall = overall,
            WinrateByLaneState = new { Ahead = Bucket(ahead), Even = Bucket(even), Behind = Bucket(behind), At15 = at15, Trajectory = laneTrajectory },
            DeathZones = Counted(deaths.Where(d => d.Zone is { Length: > 0 }).Select(d => d.Zone), deaths.Count),
            TopKillers = Counted(deaths.Select(d => d.KilledBy), deaths.Count),
            FollowIn = new
            {
                TotalDeaths = deaths.Count,
                FollowIns = followIns.Count,
                Rate = deaths.Count > 0 ? Math.Round(followIns.Count / (double)deaths.Count, 3) : 0,
                PureLoss = followIns.Count(d => d.FollowPureLoss == true),
                TwoPlusDown = followIns.Count(d => d.FollowAlliesDownBefore >= 2),
                ByRole = Counted(followIns.Select(d => d.FollowTeammateRole ?? "?"), followIns.Count),
                GoldState = new
                {
                    Behind = followIns.Count(d => d.FollowTeamGoldDiff < -1500),
                    Even = followIns.Count(d => d.FollowTeamGoldDiff is >= -1500 and <= 1500),
                    Ahead = followIns.Count(d => d.FollowTeamGoldDiff > 1500),
                },
            },
            ByChampion = SplitBy(m => m.Champion, withDetail: true),
            ByRole = SplitBy(m => m.Position),
            Series = series,
            LpDeltas = lpDeltas,
            Observations = Observations(matches, deaths, followIns, ahead, behind, overall.CsAt10, overall.VisionPerMin, overall.ControlWardsPerGame, laneByRole.Cast<dynamic>().ToList()),
        };
    }

    private static List<string> Observations(
        List<Match> matches, List<Death> deaths, List<Death> followIns,
        List<Match> ahead, List<Match> behind,
        double csAt10, double visionPerMin, double controlWards, List<dynamic> laneByRole)
    {
        var o = new List<string>();
        if (matches is not { Count: > 0 }) return o;

        var wins = matches.Count(m => m.Win);
        var champs = matches.Select(m => m.Champion).Distinct().Count();
        o.Add($"Record {wins}-{matches.Count - wins} ({Math.Round(100.0 * wins / matches.Count)} %) across {matches.Count} games on {champs} champions.");

        if (ahead is { Count: >= 5 } && behind is { Count: >= 5 })
        {
            var aw = Math.Round(100.0 * ahead.Count(m => m.Win) / ahead.Count);
            var bw = Math.Round(100.0 * behind.Count(m => m.Win) / behind.Count);
            o.Add($"When ahead by 10:00 you win {aw} % ({ahead.Count} games); when behind, {bw} % ({behind.Count}). " +
                  (aw - bw >= 10 ? "Laning leads convert." : "Leads aren't converting - look at the mid game."));
        }

        if (deaths is { Count: > 0 })
        {
            var pre10 = Math.Round(100.0 * deaths.Count(d => d.TimeSec < 600) / deaths.Count);
            var topZone = deaths.Where(d => d.Zone is { Length: > 0 }).GroupBy(d => d.Zone).OrderByDescending(g => g.Count()).FirstOrDefault();
            o.Add($"{pre10} % of deaths come before 10:00" +
                  (topZone is not null ? $"; most-common death zone is {topZone.Key} ({Math.Round(100.0 * topZone.Count() / deaths.Count)} % of deaths)." : "."));
        }

        var byChamp = matches.GroupBy(m => m.Champion).Where(g => g.Count() >= 5)
            .Select(g => new { Champ = g.Key, Games = g.Count(), Wr = Math.Round(100.0 * g.Count(m => m.Win) / g.Count()) })
            .OrderByDescending(c => c.Wr).ToList();
        if (byChamp is { Count: >= 2 })
        {
            o.Add($"Best win rate (>=5 games): {byChamp[0].Champ} {byChamp[0].Wr} % over {byChamp[0].Games}; weakest: {byChamp[^1].Champ} {byChamp[^1].Wr} % over {byChamp[^1].Games}.");
        }

        o.Add($"CS@10 averages {csAt10}; vision/min {visionPerMin}; control wards {controlWards}/game.");

        if (laneByRole is { Count: > 0 })
        {
            o.Add("Lane gold@10 by role: " + string.Join(", ", laneByRole.Select(r => $"{r.Role} {(r.Avg >= 0 ? "+" : "")}{r.Avg}")) + ".");
        }

        if (deaths is { Count: > 0 } && followIns is { Count: > 0 })
        {
            var rate = Math.Round(100.0 * followIns.Count / deaths.Count);
            var pure = followIns.Count(d => d.FollowPureLoss == true);
            o.Add($"{rate} % of deaths are follow-ins (walking in after a fallen teammate); {pure} of {followIns.Count} got nothing back.");
        }

        return o;
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
