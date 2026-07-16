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
    private sealed record AbsentMoment(int TimeSec, int OppInvolvement, string Where, int? MyDistance, bool Paid);
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
            ? new { MatchId = matchId, LaneDuel = Payload(r.Lane), Fights = Payload(r.Fights), Discipline = Payload(r.Discipline) }
            : null;
    }

    /// Light verdict triple per match for the list rows.
    public async Task<object> VerdictsAsync(string[] ids, CancellationToken ct) =>
        (await BuildAsync(ids, ct)).ToDictionary(kv => kv.Key, kv => new
        {
            LaneDuel = kv.Value.Lane?.Verdict,
            Fights = kv.Value.Fights.Verdict,
            Discipline = kv.Value.Discipline.Verdict,
        });

    private static object? Payload(Verdicted? v) => v is null ? null : new { v.Verdict, v.Detail };

    private sealed record Review(Verdicted? Lane, Verdicted Fights, Verdicted Discipline);

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
                Discipline(matchDeaths, matchObjectives, matchPositions, allyPids, me));
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

        var killsOnOpp = kills.Count(k => k.KillerParticipantId == me.ParticipantId && k.VictimParticipantId == opp.ParticipantId);
        var deathsToOpp = kills.Count(k => k.KillerParticipantId == opp.ParticipantId && k.VictimParticipantId == me.ParticipantId);

        // The opponent's impact in fights I skipped: kills/assists on my
        // teammates, split by where I was - dead, elsewhere (paid or not), or
        // right there and uninvolved.
        var absentMoments = new List<AbsentMoment>();
        var oppKillsWhileDead = 0;
        var oppKillsWhileAbsent = 0;
        var absencePaid = 0;
        var absenceUnpaid = 0;
        foreach (var f in fights.Where(f => !f.Participated))
        {
            var windowKills = kills.Where(k => k.TimeSec >= f.StartSec && k.TimeSec <= f.EndSec).ToList();
            var oppInvolved = windowKills.Count(k => allyPids.Contains(k.VictimParticipantId)
                && (k.KillerParticipantId == opp.ParticipantId || AssistedBy(k, opp.ParticipantId)));
            if (oppInvolved == 0 || windowKills.Count == 0) continue;

            var wasDead = deaths.Any(d => d.TimeSec <= f.StartSec && f.StartSec - d.TimeSec <= RespawnWindowSec);
            var cx = windowKills.Average(k => k.X);
            var cy = windowKills.Average(k => k.Y);
            var myPos = InterpolatedAt(myPositions, (f.StartSec + f.EndSec) / 2);
            var dist = myPos is { } p ? (int)Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)) : (int?)null;
            var where = wasDead ? "dead" : dist is > FarUnits ? "elsewhere" : "nearby";
            // A structure I personally took around the fight justifies being away.
            var paid = objectives.Any(o => o.ByMyTeam && o.Kind is "TOWER" or "INHIBITOR"
                && o.KillerParticipantId == me.ParticipantId
                && o.TimeSec >= f.StartSec - 30 && o.TimeSec <= f.EndSec + PaidWindowSec);

            oppKillsWhileAbsent += oppInvolved;
            if (wasDead) oppKillsWhileDead += oppInvolved;
            else if (where == "elsewhere") { if (paid) absencePaid++; else absenceUnpaid++; }

            absentMoments.Add(new AbsentMoment(f.StartSec, oppInvolved, where, dist, paid));
        }

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
        Add("they cashed in while you were dead", oppKillsWhileDead >= 2 ? -1 : 0);
        Add("unpaid absences from their fights", absenceUnpaid >= 2 ? -1 : 0);
        Add("your absences took structures", absencePaid >= 2 ? 1 : 0);
        var score = components.Sum(c => c.Delta);

        return new Verdicted(score >= 2 ? "yes" : score <= -2 ? "no" : "mixed", new
        {
            Opponent = opp.Champion,
            m.LaneGoldDiff10, m.LaneGoldDiff15,
            LateGold = late is not null ? new { late.Min, late.Gold } : null,
            KillsOnOpponent = killsOnOpp,
            DeathsToOpponent = deathsToOpp,
            OppKillsWhileDead = oppKillsWhileDead,
            OppKillsWhileAbsent = oppKillsWhileAbsent,
            AbsentMoments = absentMoments,
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

        // Enemy epics taken while I was elsewhere: the Baron-while-splitting
        // pattern. "Paid" = I took a structure around that moment (a real trade).
        var byPid = positions.GroupBy(p => p.ParticipantId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.TimeSec).ToList());
        var myPositions = me is not null ? byPid.GetValueOrDefault(me.ParticipantId) ?? [] : [];
        var concededAbsent = new List<ConcededEpic>();
        foreach (var o in objectives.Where(o => !o.ByMyTeam && EpicKinds.Contains(o.Kind)))
        {
            if (InterpolatedAt(myPositions, o.TimeSec) is not { } p) continue;
            var dist = (int)Math.Sqrt(Math.Pow(p.X - o.X, 2) + Math.Pow(p.Y - o.Y, 2));
            if (dist <= FarUnits) continue;
            var alliesNear = allyPids.Count(pid => InterpolatedAt(byPid.GetValueOrDefault(pid) ?? [], o.TimeSec) is { } ap
                && Math.Sqrt(Math.Pow(ap.X - o.X, 2) + Math.Pow(ap.Y - o.Y, 2)) <= 2500);
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
