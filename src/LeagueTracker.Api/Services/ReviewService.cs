using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// The three questions, answered per game from stored data and deliberately
/// blind to the result: did I out-duel my lane (whole game, not just laning),
/// did my fights buy the map, and did I account for the enemy before stepping.
/// Verdicts are transparent sums of named components so they can be tuned
/// against real games; the honest ceiling stays what it is everywhere else -
/// 60s-frame interpolated positions, and no ward/fog data from Riot.
public sealed class ReviewService(LeagueDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record FightDto(
        int StartSec, int EndSec, string Kind, string Result, bool Participated,
        int Allies, int Enemies, int AllyKills, int EnemyKills, int GoldSwing, bool ConvertedObjective);

    private sealed record Component(string Label, int Delta);
    /// One consequential absence: someone cashed in a fight while the other
    /// laner was dead / elsewhere / nearby-uninvolved. Where and Paid describe
    /// the ABSENT player (paid = they took a structure around that window).
    private sealed record LedgerMoment(int TimeSec, int Kills, string Where, int? Distance, bool Paid);
    private sealed record ConcededEpic(string Kind, int TimeSec, int MyDistance, int AlliesNear, bool Paid);
    private sealed record Verdicted(string? Verdict, object Detail);

    private static readonly string[] EpicKinds = ["DRAGON", "BARON", "HERALD", "GRUBS", "ATAKHAN"];

    /// A death this recent still counts as "you were down" for a fight start.
    private const int RespawnWindowSec = 45;
    /// Further than this from the action = you were elsewhere on the map.
    private const int FarUnits = 4000;
    /// A structure you took this close (in time) to the moment justifies the absence.
    private const int PaidWindowSec = 60;
    private const int LaneGoldSwing = 300;
    private const int LateGoldSwing = 500;

    public async Task<object?> GetAsync(string matchId, CancellationToken ct)
    {
        var reviews = await BuildAsync([matchId], ct);
        return reviews.TryGetValue(matchId, out var r)
            ? new
            {
                MatchId = matchId,
                Contest = Contest(r),
                LaneDuel = Payload(r.Lane),
                Fights = Payload(r.Fights),
                Discipline = Payload(r.Discipline),
                Stewardship = Payload(r.Stewardship),
            }
            : null;
    }

    /// Light verdict tuple per match for the list rows.
    public async Task<object> VerdictsAsync(string[] ids, CancellationToken ct) =>
        (await BuildAsync(ids, ct)).ToDictionary(kv => kv.Key, kv => new
        {
            Contest = Contest(kv.Value),
            LaneDuel = kv.Value.Lane?.Verdict,
            Fights = kv.Value.Fights.Verdict,
            Discipline = kv.Value.Discipline.Verdict,
            Stewardship = kv.Value.Stewardship?.Verdict,
        });

    /// Verdict strings per match for the CSV export - the same fold the list
    /// rows use, chunked so an all-history export doesn't materialize every
    /// position sample in one query.
    public async Task<Dictionary<string, (string? Contest, string? Lane, string? Fights, string? Discipline, string? Stewardship)>>
        VerdictStringsAsync(string[] ids, CancellationToken ct)
    {
        var result = new Dictionary<string, (string?, string?, string?, string?, string?)>();
        foreach (var chunk in ids.Chunk(64))
        {
            foreach (var (id, r) in await BuildAsync(chunk, ct))
            {
                result[id] = (Contest(r), r.Lane?.Verdict, r.Fights.Verdict, r.Discipline.Verdict, r.Stewardship?.Verdict);
            }
        }
        return result;
    }

    /// This many questions won with none lost (or the mirror) is the
    /// difference between winning the contest and dominating it.
    private const int SweepCount = 3;

    /// The fifth verdict, a pure fold of the four question verdicts (mixed
    /// and unanswerable questions excluded). It describes the CONTEST, never
    /// the game: you can win it in a defeat and get run over in a victory.
    /// Both ends are deliberately harsh - the honest bottom tier is the point.
    private static string? Contest(Review r)
    {
        string?[] verdicts = [r.Lane?.Verdict, r.Fights.Verdict, r.Discipline.Verdict, r.Stewardship?.Verdict];
        if (verdicts.All(v => v is null)) return null;
        var won = verdicts.Count(v => v == "yes");
        var lost = verdicts.Count(v => v == "no");
        return won >= SweepCount && lost == 0 ? "dominated"
            : lost >= SweepCount && won == 0 ? "runover"
            : won > lost ? "won"
            : lost > won ? "lost"
            : "split";
    }

    private static object? Payload(Verdicted? v) => v is null ? null : new { v.Verdict, v.Detail };

    private sealed record Review(Verdicted? Lane, Verdicted Fights, Verdicted Discipline, Verdicted? Stewardship);

    private async Task<Dictionary<string, Review>> BuildAsync(string[] ids, CancellationToken ct)
    {
        var matches = await db.Matches.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct);
        var participants = (await db.Participants.AsNoTracking().Where(p => ids.Contains(p.MatchId)).ToListAsync(ct))
            .GroupBy(p => p.MatchId).ToDictionary(g => g.Key, g => g.ToList());
        var deaths = (await db.Deaths.AsNoTracking().Where(d => ids.Contains(d.MatchId)).ToListAsync(ct))
            .GroupBy(d => d.MatchId).ToDictionary(g => g.Key, g => g.OrderBy(d => d.TimeSec).ToList());
        var kills = (await db.KillEvents.AsNoTracking().Where(k => ids.Contains(k.MatchId)).ToListAsync(ct))
            .GroupBy(k => k.MatchId).ToDictionary(g => g.Key, g => g.OrderBy(k => k.TimeSec).ToList());
        var objectives = (await db.ObjectiveEvents.AsNoTracking().Where(o => ids.Contains(o.MatchId)).ToListAsync(ct))
            .GroupBy(o => o.MatchId).ToDictionary(g => g.Key, g => g.ToList());
        var positions = (await db.PositionSamples.AsNoTracking().Where(p => ids.Contains(p.MatchId)).ToListAsync(ct))
            .GroupBy(p => p.MatchId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<string, Review>();
        foreach (var m in matches.Where(m => m.HasTimeline))
        {
            var parts = participants.GetValueOrDefault(m.Id) ?? [];
            var me = parts.FirstOrDefault(p => p.IsMe);
            var opp = parts.FirstOrDefault(p => !p.IsAlly && p.Position == m.Position && m.Position is { Length: > 0 });
            var allyPids = parts.Where(p => p.IsAlly).Select(p => p.ParticipantId).ToHashSet();
            var matchDeaths = deaths.GetValueOrDefault(m.Id) ?? [];
            var matchKills = kills.GetValueOrDefault(m.Id) ?? [];
            var matchObjectives = objectives.GetValueOrDefault(m.Id) ?? [];
            var matchPositions = positions.GetValueOrDefault(m.Id) ?? [];
            var fights = m.FightsJson is { Length: > 0 }
                ? JsonSerializer.Deserialize<List<FightDto>>(m.FightsJson, Json) ?? []
                : [];

            result[m.Id] = new Review(
                LaneDuel(m, me, opp, matchDeaths, matchKills, matchObjectives, fights, allyPids, matchPositions),
                FightsVerdict(fights),
                Discipline(matchDeaths, matchObjectives, matchPositions, allyPids, me),
                Stewardship(m));
        }
        return result;
    }

    // --- Q1: did I out-duel my lane (whole game)? -----------------------------------

    private static Verdicted? LaneDuel(
        Match m, MatchParticipant? me, MatchParticipant? opp, List<Death> deaths, List<KillEvent> kills,
        List<ObjectiveEvent> objectives, List<FightDto> fights, HashSet<int> allyPids, List<PositionSample> positions)
    {
        if (me is null || opp is null) return null;
        var myPositions = positions.Where(p => p.ParticipantId == me.ParticipantId).OrderBy(p => p.TimeSec).ToList();
        var oppPositions = positions.Where(p => p.ParticipantId == opp.ParticipantId).OrderBy(p => p.TimeSec).ToList();
        var enemyPids = new HashSet<int>(positions.Select(p => p.ParticipantId)
            .Where(pid => !allyPids.Contains(pid) && pid != me.ParticipantId));
        var oppDeathSecs = kills.Where(k => k.VictimParticipantId == opp.ParticipantId).Select(k => k.TimeSec).ToList();
        var myDeathSecs = deaths.Select(d => d.TimeSec).ToList();

        var killsOnOpp = kills.Count(k => k.KillerParticipantId == me.ParticipantId && k.VictimParticipantId == opp.ParticipantId);
        var deathsToOpp = kills.Count(k => k.KillerParticipantId == opp.ParticipantId && k.VictimParticipantId == me.ParticipantId);

        // The two-sided absence ledger: for every fight one laner skipped while
        // the other cashed in, record where the absent one was and whether the
        // absence paid (they took a structure around it). Both of you get the
        // same audit - a split-push only counts against whoever earned nothing.
        bool Involved(KillEvent k, int pid, HashSet<int> victims) =>
            victims.Contains(k.VictimParticipantId) && (k.KillerParticipantId == pid || AssistedBy(k, pid));
        bool TookStructure(int pid, bool myTeam, int startSec, int endSec) =>
            objectives.Any(o => o.ByMyTeam == myTeam && o.Kind is "TOWER" or "INHIBITOR"
                && o.KillerParticipantId == pid && o.TimeSec >= startSec - 30 && o.TimeSec <= endSec + PaidWindowSec);
        LedgerMoment? Moment(FightDto f, List<KillEvent> windowKills, int cashKills,
            List<int> absenteeDeaths, List<PositionSample> absenteePositions, int absenteePid, bool absenteeOnMyTeam)
        {
            if (cashKills == 0 || windowKills.Count == 0) return null;
            var wasDead = absenteeDeaths.Any(t => t <= f.StartSec && f.StartSec - t <= RespawnWindowSec);
            var cx = windowKills.Average(k => k.X);
            var cy = windowKills.Average(k => k.Y);
            var pos = InterpolatedAt(absenteePositions, (f.StartSec + f.EndSec) / 2);
            var dist = pos is { } p ? (int)Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)) : (int?)null;
            var where = wasDead ? "dead" : dist is > FarUnits ? "elsewhere" : "nearby";
            return new LedgerMoment(f.StartSec, cashKills, where, dist,
                TookStructure(absenteePid, absenteeOnMyTeam, f.StartSec, f.EndSec));
        }

        var theirCashIns = new List<LedgerMoment>();   // they scored, I was absent
        var myCashIns = new List<LedgerMoment>();      // I scored, they were absent
        foreach (var f in fights)
        {
            var windowKills = kills.Where(k => k.TimeSec >= f.StartSec && k.TimeSec <= f.EndSec).ToList();
            var midSec = (f.StartSec + f.EndSec) / 2;

            if (!f.Participated)
            {
                var oppCash = windowKills.Count(k => Involved(k, opp.ParticipantId, allyPids));
                if (Moment(f, windowKills, oppCash, myDeathSecs, myPositions, me.ParticipantId, true) is { } theirs)
                {
                    theirCashIns.Add(theirs);
                }
            }
            else
            {
                // The opponent "participated" if they touched a kill or stood in
                // the fight - the same headcount idea the analyzer uses for me.
                var oppThere = windowKills.Any(k => k.KillerParticipantId == opp.ParticipantId
                        || k.VictimParticipantId == opp.ParticipantId || AssistedBy(k, opp.ParticipantId))
                    || (windowKills.Count > 0 && InterpolatedAt(oppPositions, midSec) is { } op
                        && Math.Sqrt(Math.Pow(op.X - windowKills.Average(k => k.X), 2)
                            + Math.Pow(op.Y - windowKills.Average(k => k.Y), 2)) <= 2500);
                if (oppThere) continue;
                var myCash = windowKills.Count(k => Involved(k, me.ParticipantId, enemyPids));
                if (Moment(f, windowKills, myCash, oppDeathSecs, oppPositions, opp.ParticipantId, false) is { } mine)
                {
                    myCashIns.Add(mine);
                }
            }
        }

        var theirCashKills = theirCashIns.Sum(x => x.Kills);
        var myCashKills = myCashIns.Sum(x => x.Kills);
        var myUnpaidAbsences = theirCashIns.Count(x => x.Where == "elsewhere" && !x.Paid);
        var theirUnpaidAbsences = myCashIns.Count(x => x.Where == "elsewhere" && !x.Paid);

        var checkpoints = m.LaneDiffsJson is { Length: > 0 }
            ? JsonSerializer.Deserialize<List<TimelineAnalyzer.LaneDiffPoint>>(m.LaneDiffsJson, Json) ?? []
            : [];
        var late = checkpoints.Where(c => c.Min is 20 or 25 or 30).OrderByDescending(c => c.Min).FirstOrDefault();

        // Transparent verdict: named components, each worth +/-1, summed.
        var components = new List<Component>();
        void Add(string label, int delta) { if (delta != 0) components.Add(new Component(label, delta)); }
        var laneGold = m.LaneGoldDiff15 ?? m.LaneGoldDiff10;
        Add($"lane gold @{(m.LaneGoldDiff15 is not null ? 15 : 10)}",
            laneGold is { } lg ? (lg >= LaneGoldSwing ? 1 : lg <= -LaneGoldSwing ? -1 : 0) : 0);
        Add($"gold vs lane @{late?.Min}",
            late is not null ? (late.Gold >= LateGoldSwing ? 1 : late.Gold <= -LateGoldSwing ? -1 : 0) : 0);
        Add("kill exchange with laner", Math.Sign(killsOnOpp - deathsToOpp));
        Add("cash-ins while the other was away", Math.Sign(myCashKills - theirCashKills));
        Add("unpaid absences, theirs vs yours", Math.Sign(theirUnpaidAbsences - myUnpaidAbsences));
        var score = components.Sum(c => c.Delta);

        return new Verdicted(score >= 2 ? "yes" : score <= -2 ? "no" : "mixed", new
        {
            Opponent = opp.Champion,
            m.LaneGoldDiff10, m.LaneGoldDiff15,
            LateGold = late is not null ? new { late.Min, late.Gold } : null,
            KillsOnOpponent = killsOnOpp,
            DeathsToOpponent = deathsToOpp,
            TheirCashKills = theirCashKills,
            MyCashKills = myCashKills,
            TheirCashIns = theirCashIns,
            MyCashIns = myCashIns,
            Components = components,
        });
    }

    // --- Q2: did my fights buy the map? ---------------------------------------------

    private static Verdicted FightsVerdict(List<FightDto> fights)
    {
        var mine = fights.Where(f => f.Participated).ToList();
        var won = mine.Count(f => f.Result == "won");
        var lost = mine.Count(f => f.Result == "lost");
        var converted = mine.Count(f => f.Result == "won" && f.ConvertedObjective);
        var conceded = mine.Count(f => f.Result == "lost" && f.ConvertedObjective);

        string? verdict = won + lost == 0 ? null
            : won > lost && converted * 2 >= won ? "yes"
            : lost > won || conceded > converted ? "no"
            : "mixed";

        return new Verdicted(verdict, new
        {
            Participated = mine.Count,
            Won = won,
            Lost = lost,
            Converted = converted,
            Conceded = conceded,
        });
    }

    // --- Q4: did I keep my lead / recover my deficit? --------------------------------

    private static Verdicted? Stewardship(Match m)
    {
        var checkpoints = m.LaneDiffsJson is { Length: > 0 }
            ? JsonSerializer.Deserialize<List<TimelineAnalyzer.LaneDiffPoint>>(m.LaneDiffsJson, Json) ?? []
            : [];
        var start = checkpoints.FirstOrDefault(c => c.Min == 10);
        var end = checkpoints.Where(c => c.Min is 20 or 25 or 30).OrderByDescending(c => c.Min).FirstOrDefault();
        if (start is null || end is null) return null;

        var state = start.Gold >= LaneGoldSwing ? "ahead" : start.Gold <= -LaneGoldSwing ? "behind" : "even";
        var (verdict, summary) = state switch
        {
            "ahead" => end.Gold >= start.Gold + LaneGoldSwing ? ("yes", "lead grew")
                : end.Gold >= LaneGoldSwing ? ("yes", "lead held")
                : end.Gold <= -LaneGoldSwing ? ("no", "lead flipped")
                : ("mixed", "lead drifted to even"),
            "behind" => end.Gold >= -LaneGoldSwing ? ("yes", "deficit recovered")
                : end.Gold >= start.Gold + LaneGoldSwing ? ("mixed", "deficit reduced")
                : end.Gold <= start.Gold - LaneGoldSwing ? ("no", "deficit grew")
                : ("mixed", "deficit held"),
            _ => end.Gold >= LaneGoldSwing ? ("yes", "pulled ahead")
                : end.Gold <= -LaneGoldSwing ? ("no", "fell behind")
                : ("mixed", "stayed even"),
        };

        return new Verdicted(verdict, new
        {
            StartMin = start.Min, StartGold = start.Gold,
            EndMin = end.Min, EndGold = end.Gold,
            State = state,
            Summary = summary,
            TeamGold15 = m.TeamGoldDiff15,
            TeamGold20 = m.TeamGoldDiff20,
        });
    }

    // --- Q3: did I account for the enemy before I stepped? --------------------------

    private static Verdicted Discipline(
        List<Death> deaths, List<ObjectiveEvent> objectives,
        List<PositionSample> positions, HashSet<int> allyPids, MatchParticipant? me)
    {
        var ganked = 0;
        var followIns = 0;
        var isolated = 0;
        var withTeam = 0;
        foreach (var d in deaths)
        {
            if (d.EnemyJunglerNear == true && d.TimeSec < 840) ganked++;
            else if (d.FollowTeammate is not null) followIns++;
            else if (d is { EnemiesNearDeath: >= 2, AlliesNearDeath: 0 }) isolated++;
            else withTeam++;
        }

        // Enemy epics taken while I was elsewhere AND my team contested it
        // short-handed (2+ allies there) - the Baron-while-splitting pattern.
        // Uncontested concessions are team macro calls, not personal discipline,
        // so they never appear here. "Paid" = I took a structure around that
        // moment (a real trade). Same-kind events within 90s (grub spawns)
        // collapse into one moment.
        var byPid = positions.GroupBy(p => p.ParticipantId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.TimeSec).ToList());
        var myPositions = me is not null ? byPid.GetValueOrDefault(me.ParticipantId) ?? [] : [];
        var concededAbsent = new List<ConcededEpic>();
        foreach (var o in objectives.Where(o => !o.ByMyTeam && EpicKinds.Contains(o.Kind)).OrderBy(o => o.TimeSec))
        {
            if (concededAbsent.Any(c => c.Kind == o.Kind && o.TimeSec - c.TimeSec <= 90)) continue;
            if (InterpolatedAt(myPositions, o.TimeSec) is not { } p) continue;
            var dist = (int)Math.Sqrt(Math.Pow(p.X - o.X, 2) + Math.Pow(p.Y - o.Y, 2));
            if (dist <= FarUnits) continue;
            var alliesNear = allyPids.Count(pid => InterpolatedAt(byPid.GetValueOrDefault(pid) ?? [], o.TimeSec) is { } ap
                && Math.Sqrt(Math.Pow(ap.X - o.X, 2) + Math.Pow(ap.Y - o.Y, 2)) <= 2500);
            if (alliesNear < 2) continue;
            var paid = me is not null && objectives.Any(x => x.ByMyTeam && x.Kind is "TOWER" or "INHIBITOR"
                && x.KillerParticipantId == me.ParticipantId
                && Math.Abs(x.TimeSec - o.TimeSec) <= PaidWindowSec);
            concededAbsent.Add(new ConcededEpic(o.Kind, o.TimeSec, dist, alliesNear, paid));
        }

        var bad = ganked + followIns + isolated;
        var unpaidConcessions = concededAbsent.Count(c => !c.Paid);
        var verdict = bad == 0 && unpaidConcessions == 0 ? "yes"
            : bad >= 3 || unpaidConcessions >= 2 || (bad >= 2 && unpaidConcessions >= 1) ? "no"
            : "mixed";

        return new Verdicted(verdict, new
        {
            Deaths = deaths.Count,
            Ganked = ganked,
            FollowIns = followIns,
            Isolated = isolated,
            WithTeam = withTeam,
            ConcededEpicsAbsent = concededAbsent,
        });
    }

    private static bool AssistedBy(KillEvent k, int pid) =>
        k.AssistIds is { Length: > 0 } && k.AssistIds.Split(',').Contains(pid.ToString());

    /// Linear interpolation over one participant's 60s samples - the same
    /// honest estimate the analyzer uses everywhere.
    private static (int X, int Y)? InterpolatedAt(List<PositionSample> samples, int atSec)
    {
        if (samples is not { Count: > 0 }) return null;
        var before = samples.LastOrDefault(s => s.TimeSec <= atSec);
        var after = samples.FirstOrDefault(s => s.TimeSec > atSec);
        if (before is null) return (after!.X, after.Y);
        if (after is null) return (before.X, before.Y);
        var span = after.TimeSec - before.TimeSec;
        var alpha = span <= 0 ? 0 : (double)(atSec - before.TimeSec) / span;
        return ((int)(before.X + (after.X - before.X) * alpha), (int)(before.Y + (after.Y - before.Y) * alpha));
    }
}
