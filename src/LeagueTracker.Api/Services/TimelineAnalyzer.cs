using System.Text.Json;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;

namespace LeagueTracker.Api.Services;

public sealed class TimelineAnalysis
{
    public List<Death> Deaths { get; init; } = [];
    public List<PositionSample> Positions { get; init; } = [];
    public List<KillEvent> Kills { get; init; } = [];
    public List<ObjectiveEvent> Objectives { get; init; } = [];
    public List<ItemEvent> ItemEvents { get; init; } = [];
    public double? TimeInEnemyHalfPct { get; init; }
    public int? AvgNearestAllyDist { get; init; }
    public int? CsAt10 { get; init; }
    public int? CsAt14 { get; init; }
    public int? CsAt15 { get; init; }
    public int? LaneGoldDiff10 { get; init; }
    public int? LaneXpDiff10 { get; init; }
    public int? LaneCsDiff10 { get; init; }
    public int? LaneGoldDiff15 { get; init; }
    public int? LaneXpDiff15 { get; init; }
    public int? LaneCsDiff15 { get; init; }
    public bool? FirstToLevel2 { get; init; }
    public List<TimelineAnalyzer.LaneDiffPoint> LaneDiffs { get; init; } = [];
    public string SkillOrder { get; init; } = "";
    public double? DpmEarly { get; init; }
    public double? DpmMid { get; init; }
    public double? DpmLate { get; init; }
    // Unspent gold (macro): what I walked around without spending.
    public int? AvgUnspentGold { get; init; }
    public int? MaxUnspentGold { get; init; }
    // Ward cadence (vision): when and how much proactive vision I set up.
    public int? FirstWardSec { get; init; }
    public int? FirstControlWardSec { get; init; }
    public int WardsFirst10 { get; init; }
    // Power-spike level timing vs the lane opponent (seconds; + = I hit it first).
    public int? Level6LeadSec { get; init; }
    public int? Level11LeadSec { get; init; }
    public int? Level16LeadSec { get; init; }
    // Objective presence: friendly epic objectives I was actually near when taken.
    public int FriendlyEpicObjectives { get; init; }
    public int ObjectivesPresentFor { get; init; }
}

/// Everything derivable from one timeline pass. Frames arrive every 60s, so any
/// position at an event timestamp is a linear interpolation between the two
/// bounding frames - coarse by nature, and the honest ceiling of the API.
public static class TimelineAnalyzer
{
    /// Within this range of the death spot a player counts as "there".
    private const int NearRadius = 2000;
    /// Summoner's Rift is ~14800x14800; the diagonal x+y splits the halves.
    private const int MapDiagonal = 14800;
    /// A death within this window after a friendly objective reads as overstay-to-force-more.
    private const int PostObjectiveWindowSec = 90;

    // Follow-in parameters: a teammate fell within this many seconds before me,
    // within this range of THEIR death spot; "traded" looks ahead this long.
    private const int FollowWindowSec = 15;
    private const int FollowNearUnits = 2500;
    private const int FollowTradeAfterSec = 10;

    private const int ObjectiveNearUnits = 2500;

    private sealed record FrameStats(int Cs, int Gold, int Xp, int Level, int CurrentGold, int DmgToChamps);

    /// One lane-diff checkpoint (mine minus the same-role enemy's) for the
    /// laning table: minutes 10/15/20/25 where the game lasted that long.
    /// Item lists are replayed inventories (purchases minus consumed/sold/undone).
    public sealed record LaneDiffPoint(
        int Min, int Gold, int Xp, int Cs, int Level,
        int MyCs, int MyLevel, int OppCs, int OppLevel,
        List<int> MyItems, List<int> OppItems,
        // Running whole-game kill scores at the checkpoint - the "why" behind
        // a gold swing. Defaults keep pre-existing serialized rows readable.
        int MyKills = 0, int MyDeaths = 0, int OppKills = 0, int OppDeaths = 0);

    private readonly record struct ItemLogEntry(int T, int Pid, string Kind, int ItemId, int BeforeId, int AfterId);

    private sealed record Frame(int TimeSec, Dictionary<int, (int X, int Y)> Positions, Dictionary<int, FrameStats> Stats);

    public static TimelineAnalysis Analyze(string timelineRaw, MatchInfoDto info, MatchParticipantDto me)
    {
        using var doc = JsonDocument.Parse(timelineRaw);
        if (!doc.RootElement.TryGetProperty("info", out var tlInfo) || !tlInfo.TryGetProperty("frames", out var framesEl))
        {
            return new TimelineAnalysis();
        }

        var champByPid = info.Participants.ToDictionary(p => p.ParticipantId, p => p.ChampionName);
        var enemyPids = info.Participants.Where(p => p.TeamId != me.TeamId).Select(p => p.ParticipantId).ToHashSet();
        var allyPids = info.Participants.Where(p => p.TeamId == me.TeamId && p.ParticipantId != me.ParticipantId)
            .Select(p => p.ParticipantId).ToHashSet();

        var frames = ParseFrames(framesEl);
        var positions = frames
            .SelectMany(f => f.Positions.Select(kv => new PositionSample
            {
                ParticipantId = kv.Key, TimeSec = f.TimeSec, X = kv.Value.X, Y = kv.Value.Y,
            }))
            .ToList();

        var kills = new List<KillEvent>();
        var objectives = new List<ObjectiveEvent>();
        var itemEvents = new List<ItemEvent>();
        var deaths = new List<Death>();
        var skillOrder = new List<int>();
        var levelAt = new Dictionary<(int Pid, int Level), int>();   // when each player reached each level
        var itemLog = new List<ItemLogEntry>();        // mine + lane opponent's, for inventory replay
        var myWardSecs = new List<(int Sec, string Type)>();         // wards I placed

        var opp = info.Participants.FirstOrDefault(p =>
            p.TeamId != me.TeamId && p.TeamPosition == me.TeamPosition && me.TeamPosition is { Length: > 0 });

        foreach (var frame in framesEl.EnumerateArray())
        {
            if (!frame.TryGetProperty("events", out var events)) continue;
            foreach (var ev in events.EnumerateArray())
            {
                switch (ev.GetProperty("type").GetString())
                {
                    case "SKILL_LEVEL_UP":
                        if (ev.TryGetProperty("participantId", out var sp) && sp.GetInt32() == me.ParticipantId
                            && ev.TryGetProperty("skillSlot", out var slot))
                        {
                            skillOrder.Add(slot.GetInt32());
                        }
                        break;
                    case "LEVEL_UP":
                        if (ev.TryGetProperty("level", out var lu) && ev.TryGetProperty("participantId", out var lp))
                        {
                            levelAt.TryAdd((lp.GetInt32(), lu.GetInt32()), TimeSecOf(ev));
                        }
                        break;
                    case "WARD_PLACED":
                        if (ev.TryGetProperty("creatorId", out var wc) && wc.GetInt32() == me.ParticipantId)
                        {
                            myWardSecs.Add((TimeSecOf(ev), ev.TryGetProperty("wardType", out var wt) ? wt.GetString() ?? "" : ""));
                        }
                        break;
                    case "CHAMPION_KILL":
                        var kill = ParseKill(ev);
                        kills.Add(kill);
                        if (kill.VictimParticipantId == me.ParticipantId)
                        {
                            deaths.Add(ParseDeath(ev, frame, me, champByPid));
                        }
                        break;
                    case "BUILDING_KILL":
                        objectives.Add(ParseBuildingKill(ev, me.TeamId));
                        break;
                    case "ELITE_MONSTER_KILL":
                        objectives.Add(ParseMonsterKill(ev, me.TeamId));
                        break;
                    case "ITEM_PURCHASED" or "ITEM_SOLD" or "ITEM_UNDO" or "ITEM_DESTROYED":
                    {
                        if (!ev.TryGetProperty("participantId", out var ip)) break;
                        var pid = ip.GetInt32();
                        var kind = ev.GetProperty("type").GetString()!;
                        var itemId = ev.TryGetProperty("itemId", out var ii) ? ii.GetInt32() : 0;
                        var beforeId = ev.TryGetProperty("beforeId", out var bi) ? bi.GetInt32() : 0;
                        var afterId = ev.TryGetProperty("afterId", out var ai) ? ai.GetInt32() : 0;
                        if (pid == me.ParticipantId && kind is not "ITEM_DESTROYED")
                        {
                            itemEvents.Add(new ItemEvent
                            {
                                TimeSec = TimeSecOf(ev),
                                Kind = kind.Replace("ITEM_", ""),
                                ItemId = itemId != 0 ? itemId : beforeId,
                            });
                        }
                        if (pid == me.ParticipantId || pid == opp?.ParticipantId)
                        {
                            itemLog.Add(new ItemLogEntry(TimeSecOf(ev), pid, kind, itemId, beforeId, afterId));
                        }
                        break;
                    }
                }
            }
        }

        var pidToTeam = info.Participants.ToDictionary(p => p.ParticipantId, p => p.TeamId);
        var pidToRole = info.Participants.ToDictionary(p => p.ParticipantId, p => RoleLabel(p.TeamPosition));

        foreach (var death in deaths)
        {
            death.Zone = MapZones.Classify(death.X, death.Y);
            EnrichWithConvergence(death, frames, enemyPids, allyPids);
            EnrichWithObjectiveContext(death, objectives);
            EnrichWithFollowIn(death, kills, frames, me, champByPid, pidToTeam, pidToRole);
        }

        var (enemyHalfPct, avgNearestAlly) = MovementMetrics(frames, me, allyPids);

        // Lane diffs vs the same-role enemy, read from the minute-frames.
        var f10 = FrameAtMinute(frames, 10);
        var f14 = FrameAtMinute(frames, 14);
        var f15 = FrameAtMinute(frames, 15);
        var f20 = FrameAtMinute(frames, 20);
        var last = frames.Count > 0 ? frames[^1] : null;
        FrameStats? My(Frame? f) => f is not null && f.Stats.TryGetValue(me.ParticipantId, out var s) ? s : null;
        FrameStats? Opp(Frame? f) => opp is not null && f is not null && f.Stats.TryGetValue(opp.ParticipantId, out var s) ? s : null;

        var (my10, opp10) = (My(f10), Opp(f10));
        var (my15, opp15) = (My(f15), Opp(f15));
        var my20 = My(f20);
        var myEnd = My(last);

        // Power-spike level timing vs the lane opponent (+ seconds = I hit it first).
        int? LevelLead(int level)
        {
            if (opp is null || !levelAt.TryGetValue((me.ParticipantId, level), out var mineSec)) return null;
            return levelAt.TryGetValue((opp.ParticipantId, level), out var theirsSec) ? theirsSec - mineSec : mineSec;
        }
        var firstToLevel2 = LevelLead(2) is { } l2 ? l2 >= 0 : (bool?)null;

        // Ward cadence (excludes the auto-undo/placed-then-replaced noise by just
        // taking earliest placements; blue trinket is a sweeper, not vision).
        var visionWards = myWardSecs.Where(w => w.Type is not "TEEMO_MUSHROOM" and not "UNDEFINED").ToList();
        int? firstWardSec = visionWards is { Count: > 0 } ? visionWards.Min(w => w.Sec) : null;
        var controlWardSecs = myWardSecs.Where(w => w.Type == "CONTROL_WARD").Select(w => w.Sec).ToList();
        int? firstControlWardSec = controlWardSecs is { Count: > 0 } ? controlWardSecs.Min() : null;
        var wardsFirst10 = visionWards.Count(w => w.Sec < 600);

        // Unspent gold across the game (skip the base-camp opening frames).
        var goldFrames = frames.Where(f => f.TimeSec >= 90 && f.Stats.ContainsKey(me.ParticipantId))
            .Select(f => f.Stats[me.ParticipantId].CurrentGold).ToList();
        int? avgUnspentGold = goldFrames is { Count: > 0 } ? (int)goldFrames.Average() : null;
        int? maxUnspentGold = goldFrames is { Count: > 0 } ? goldFrames.Max() : null;

        // Objective presence: friendly epic objectives I was actually near when taken.
        var friendlyEpics = objectives.Where(o => o.ByMyTeam && o.Kind is "DRAGON" or "BARON" or "HERALD" or "GRUBS" or "ATAKHAN").ToList();
        var objectivesPresentFor = 0;
        foreach (var obj in friendlyEpics)
        {
            var pos = InterpolatedPosition(frames, me.ParticipantId, obj.TimeSec);
            if (pos is { } p && Math.Sqrt(Math.Pow(p.X - obj.X, 2) + Math.Pow(p.Y - obj.Y, 2)) <= ObjectiveNearUnits)
            {
                objectivesPresentFor++;
            }
        }

        // Every 3 minutes (the item race wants density) plus the 10/15/20/25
        // milestones the lane-diff table is built around; the UI filters.
        var laneDiffs = new List<LaneDiffPoint>();
        foreach (var minute in Enumerable.Range(1, 15).Select(i => i * 3).Concat([10, 15, 20, 25]).Distinct().Order())
        {
            var frame = FrameAtMinute(frames, minute);
            if (My(frame) is not { } mineAt || Opp(frame) is not { } oppAt) continue;
            var cutoff = minute * 60;
            laneDiffs.Add(new LaneDiffPoint(minute,
                mineAt.Gold - oppAt.Gold, mineAt.Xp - oppAt.Xp, mineAt.Cs - oppAt.Cs, mineAt.Level - oppAt.Level,
                mineAt.Cs, mineAt.Level, oppAt.Cs, oppAt.Level,
                InventoryAt(itemLog, me.ParticipantId, cutoff),
                InventoryAt(itemLog, opp!.ParticipantId, cutoff),
                MyKills: kills.Count(k => k.TimeSec <= cutoff && k.KillerParticipantId == me.ParticipantId),
                MyDeaths: kills.Count(k => k.TimeSec <= cutoff && k.VictimParticipantId == me.ParticipantId),
                OppKills: kills.Count(k => k.TimeSec <= cutoff && k.KillerParticipantId == opp.ParticipantId),
                OppDeaths: kills.Count(k => k.TimeSec <= cutoff && k.VictimParticipantId == opp.ParticipantId)));
        }

        double? dpmEarly = my10 is not null ? Math.Round(my10.DmgToChamps / 10.0, 1) : null;
        double? dpmMid = my10 is not null && my20 is not null ? Math.Round((my20.DmgToChamps - my10.DmgToChamps) / 10.0, 1) : null;
        double? dpmLate = my20 is not null && myEnd is not null && last!.TimeSec > 20 * 60 + 30
            ? Math.Round((myEnd.DmgToChamps - my20.DmgToChamps) / ((last.TimeSec - 1200) / 60.0), 1) : null;

        return new TimelineAnalysis
        {
            Deaths = deaths,
            Positions = positions,
            Kills = kills,
            Objectives = objectives,
            ItemEvents = itemEvents,
            TimeInEnemyHalfPct = enemyHalfPct,
            AvgNearestAllyDist = avgNearestAlly,
            CsAt10 = my10?.Cs,
            CsAt14 = My(f14)?.Cs,
            CsAt15 = my15?.Cs,
            LaneGoldDiff10 = my10 is not null && opp10 is not null ? my10.Gold - opp10.Gold : null,
            LaneXpDiff10 = my10 is not null && opp10 is not null ? my10.Xp - opp10.Xp : null,
            LaneCsDiff10 = my10 is not null && opp10 is not null ? my10.Cs - opp10.Cs : null,
            LaneGoldDiff15 = my15 is not null && opp15 is not null ? my15.Gold - opp15.Gold : null,
            LaneXpDiff15 = my15 is not null && opp15 is not null ? my15.Xp - opp15.Xp : null,
            LaneCsDiff15 = my15 is not null && opp15 is not null ? my15.Cs - opp15.Cs : null,
            FirstToLevel2 = firstToLevel2,
            LaneDiffs = laneDiffs,
            SkillOrder = string.Join(',', skillOrder),
            DpmEarly = dpmEarly,
            DpmMid = dpmMid,
            DpmLate = dpmLate,
            AvgUnspentGold = avgUnspentGold,
            MaxUnspentGold = maxUnspentGold,
            FirstWardSec = firstWardSec,
            FirstControlWardSec = firstControlWardSec,
            WardsFirst10 = wardsFirst10,
            Level6LeadSec = LevelLead(6),
            Level11LeadSec = LevelLead(11),
            Level16LeadSec = LevelLead(16),
            FriendlyEpicObjectives = friendlyEpics.Count,
            ObjectivesPresentFor = objectivesPresentFor,
        };
    }

    private static string RoleLabel(string pos) => pos switch
    {
        "TOP" => "Top", "JUNGLE" => "Jungle", "MIDDLE" => "Mid", "BOTTOM" => "ADC", "UTILITY" => "Support",
        _ => pos is { Length: > 0 } ? pos : "?",
    };

    /// Did I walk in right after a teammate fell? Trigger = the most recent ally
    /// death inside the window; it counts when I died near THEIR death spot.
    private static void EnrichWithFollowIn(
        Death death, List<KillEvent> kills, List<Frame> frames, MatchParticipantDto me,
        Dictionary<int, string> champByPid, Dictionary<int, int> pidToTeam, Dictionary<int, string> pidToRole)
    {
        var alliesBefore = kills
            .Where(k => k.VictimParticipantId != me.ParticipantId
                && pidToTeam.GetValueOrDefault(k.VictimParticipantId) == me.TeamId
                && k.TimeSec >= death.TimeSec - FollowWindowSec && k.TimeSec < death.TimeSec)
            .OrderBy(k => k.TimeSec)
            .ToList();
        if (alliesBefore is not { Count: > 0 }) return;

        var trigger = alliesBefore[^1];
        var distance = (int)Math.Round(Math.Sqrt(Math.Pow(death.X - trigger.X, 2) + Math.Pow(death.Y - trigger.Y, 2)));
        if (distance > FollowNearUnits) return;

        var traded = kills.Any(k => pidToTeam.GetValueOrDefault(k.VictimParticipantId) != me.TeamId
            && k.TimeSec >= trigger.TimeSec && k.TimeSec <= death.TimeSec + FollowTradeAfterSec);

        death.FollowTeammate = champByPid.GetValueOrDefault(trigger.VictimParticipantId, "?");
        death.FollowTeammateRole = pidToRole.GetValueOrDefault(trigger.VictimParticipantId, "?");
        death.FollowTeammateCaughtBy = trigger.KillerParticipantId > 0
            ? champByPid.GetValueOrDefault(trigger.KillerParticipantId, "minion/turret") : "minion/turret";
        death.FollowSecondsAfter = death.TimeSec - trigger.TimeSec;
        death.FollowDistance = distance;
        death.FollowAlliesDownBefore = alliesBefore.Count;
        death.FollowPureLoss = !traded;

        var frameBefore = frames.LastOrDefault(f => f.TimeSec <= death.TimeSec);
        if (frameBefore is not null)
        {
            var mine = 0;
            var theirs = 0;
            foreach (var (pid, stats) in frameBefore.Stats)
            {
                if (pidToTeam.GetValueOrDefault(pid) == me.TeamId) mine += stats.Gold; else theirs += stats.Gold;
            }
            death.FollowTeamGoldDiff = mine - theirs;
        }
    }

    private static List<Frame> ParseFrames(JsonElement framesEl)
    {
        var frames = new List<Frame>();
        foreach (var frame in framesEl.EnumerateArray())
        {
            if (!frame.TryGetProperty("participantFrames", out var pf)) continue;
            var positions = new Dictionary<int, (int, int)>();
            var stats = new Dictionary<int, FrameStats>();
            foreach (var prop in pf.EnumerateObject())
            {
                var pid = int.Parse(prop.Name);
                if (prop.Value.TryGetProperty("position", out var pos))
                {
                    positions[pid] = (pos.GetProperty("x").GetInt32(), pos.GetProperty("y").GetInt32());
                }
                var cs = (prop.Value.TryGetProperty("minionsKilled", out var mk) ? mk.GetInt32() : 0)
                       + (prop.Value.TryGetProperty("jungleMinionsKilled", out var jk) ? jk.GetInt32() : 0);
                var gold = prop.Value.TryGetProperty("totalGold", out var tg) ? tg.GetInt32() : 0;
                var currentGold = prop.Value.TryGetProperty("currentGold", out var cg) ? cg.GetInt32() : 0;
                var xp = prop.Value.TryGetProperty("xp", out var xpEl) ? xpEl.GetInt32() : 0;
                var level = prop.Value.TryGetProperty("level", out var lvl) ? lvl.GetInt32() : 0;
                var dmg = prop.Value.TryGetProperty("damageStats", out var ds)
                    && ds.TryGetProperty("totalDamageDoneToChampions", out var dd) ? dd.GetInt32() : 0;
                stats[pid] = new FrameStats(cs, gold, xp, level, currentGold, dmg);
            }
            frames.Add(new Frame(TimeSecOf(frame), positions, stats));
        }
        return frames;
    }

    private static Frame? FrameAtMinute(List<Frame> frames, int minute) =>
        frames.FirstOrDefault(f => Math.Abs(f.TimeSec - minute * 60) <= 2);

    /// A player's position at an arbitrary second, linearly interpolated between
    /// the two bounding 60s frames (an estimate, like every position query here).
    private static (int X, int Y)? InterpolatedPosition(List<Frame> frames, int pid, int atSec)
    {
        var before = frames.LastOrDefault(f => f.TimeSec <= atSec);
        var after = frames.FirstOrDefault(f => f.TimeSec > atSec);
        (int, int)? p0 = before is not null && before.Positions.TryGetValue(pid, out var b) ? b : null;
        (int, int)? p1 = after is not null && after.Positions.TryGetValue(pid, out var a) ? a : null;
        if (p0 is null || p1 is null) return p0 ?? p1;
        var span = after!.TimeSec - before!.TimeSec;
        var alpha = span <= 0 ? 0 : (double)(atSec - before.TimeSec) / span;
        return ((int)(p0.Value.Item1 + (p1.Value.Item1 - p0.Value.Item1) * alpha),
                (int)(p0.Value.Item2 + (p1.Value.Item2 - p0.Value.Item2) * alpha));
    }

    /// Replays a player's item transactions to a point in time. Consumed
    /// components arrive as ITEM_DESTROYED; UNDO reverses its recorded before/
    /// after pair. An estimate, but a faithful one.
    private static List<int> InventoryAt(List<ItemLogEntry> log, int pid, int atSec)
    {
        var inventory = new List<int>();
        foreach (var e in log.Where(e => e.Pid == pid && e.T <= atSec).OrderBy(e => e.T))
        {
            switch (e.Kind)
            {
                case "ITEM_PURCHASED":
                    if (e.ItemId > 0) inventory.Add(e.ItemId);
                    break;
                case "ITEM_DESTROYED" or "ITEM_SOLD":
                    inventory.Remove(e.ItemId);
                    break;
                case "ITEM_UNDO":
                    if (e.BeforeId > 0)
                    {
                        var idx = inventory.LastIndexOf(e.BeforeId);
                        if (idx >= 0) inventory.RemoveAt(idx);
                    }
                    if (e.AfterId > 0) inventory.Add(e.AfterId);
                    break;
            }
        }
        return inventory;
    }

    private static KillEvent ParseKill(JsonElement ev) => new()
    {
        TimeSec = TimeSecOf(ev),
        KillerParticipantId = ev.TryGetProperty("killerId", out var k) ? k.GetInt32() : 0,
        VictimParticipantId = ev.GetProperty("victimId").GetInt32(),
        X = ev.TryGetProperty("position", out var p) ? p.GetProperty("x").GetInt32() : 0,
        Y = ev.TryGetProperty("position", out var p2) ? p2.GetProperty("y").GetInt32() : 0,
    };

    private static Death ParseDeath(JsonElement ev, JsonElement frame, MatchParticipantDto me, Dictionary<int, string> champByPid)
    {
        var killerId = ev.TryGetProperty("killerId", out var k) ? k.GetInt32() : 0;
        List<string> assistChamps = ev.TryGetProperty("assistingParticipantIds", out var assists)
            ? assists.EnumerateArray().Select(a => champByPid.GetValueOrDefault(a.GetInt32())).Where(c => c is not null).Cast<string>().ToList()
            : [];

        var death = new Death
        {
            TimeSec = TimeSecOf(ev),
            X = ev.TryGetProperty("position", out var pos) ? pos.GetProperty("x").GetInt32() : 0,
            Y = ev.TryGetProperty("position", out var pos2) ? pos2.GetProperty("y").GetInt32() : 0,
            KilledBy = killerId > 0 ? champByPid.GetValueOrDefault(killerId, "?") : "Minion/Turret/Execute",
            AssistedBy = string.Join(", ", assistChamps),
            Bounty = ev.TryGetProperty("bounty", out var b) ? b.GetInt32() : 0,
            Shutdown = ev.TryGetProperty("shutdownBounty", out var sb) ? sb.GetInt32() : 0,
        };

        if (frame.TryGetProperty("participantFrames", out var pf) && pf.TryGetProperty(me.ParticipantId.ToString(), out var mf))
        {
            if (mf.TryGetProperty("level", out var lvl)) death.MyLevel = lvl.GetInt32();
            if (mf.TryGetProperty("totalGold", out var tg)) death.MyTotalGold = tg.GetInt32();
            if (mf.TryGetProperty("minionsKilled", out var mk) && mf.TryGetProperty("jungleMinionsKilled", out var jk))
            {
                death.MyCs = mk.GetInt32() + jk.GetInt32();
            }
        }

        // The full damage ledger of the last ~10s: burst-from-fog and
        // whittled-down-overstaying look identical in a name list but not here.
        if (ev.TryGetProperty("victimDamageReceived", out var dmg))
        {
            foreach (var d in dmg.EnumerateArray())
            {
                var sourcePid = d.TryGetProperty("participantId", out var sp) ? sp.GetInt32() : 0;
                death.DamageInstances.Add(new DeathDamage
                {
                    Source = sourcePid > 0 ? champByPid.GetValueOrDefault(sourcePid, d.GetProperty("name").GetString() ?? "?")
                        : d.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
                    SpellName = d.TryGetProperty("spellName", out var sn) ? sn.GetString() ?? "" : "",
                    Physical = d.TryGetProperty("physicalDamage", out var ph) ? ph.GetInt32() : 0,
                    Magic = d.TryGetProperty("magicDamage", out var mg) ? mg.GetInt32() : 0,
                    TrueDamage = d.TryGetProperty("trueDamage", out var tr) ? tr.GetInt32() : 0,
                });
            }

            var champInstances = death.DamageInstances.Where(i => champByPid.ContainsValue(i.Source)).ToList();
            death.DamageInstanceCount = death.DamageInstances.Count;
            death.TotalDamageReceived = death.DamageInstances.Sum(i => i.Total);
            var bySource = champInstances.GroupBy(i => i.Source)
                .Select(g => new { Source = g.Key, Total = g.Sum(i => i.Total) })
                .OrderByDescending(g => g.Total)
                .ToList();
            if (bySource is { Count: > 0 } && death.TotalDamageReceived > 0)
            {
                death.TopSource = bySource[0].Source;
                death.TopSourceShare = Math.Round((double)bySource[0].Total / death.TotalDamageReceived.Value, 3);
            }
            death.DamageFrom = string.Join(", ", bySource.Select(g => g.Source));
            death.EnemiesOnYou = bySource.Count;
        }

        return death;
    }

    private static ObjectiveEvent ParseBuildingKill(JsonElement ev, int myTeamId)
    {
        // BUILDING_KILL's teamId is the team that LOST the building.
        var victimTeam = ev.TryGetProperty("teamId", out var t) ? t.GetInt32() : 0;
        var buildingType = ev.TryGetProperty("buildingType", out var bt) ? bt.GetString() : "";
        return new ObjectiveEvent
        {
            TimeSec = TimeSecOf(ev),
            Kind = buildingType == "INHIBITOR_BUILDING" ? "INHIBITOR" : "TOWER",
            SubKind = ev.TryGetProperty("towerType", out var tt) ? (tt.GetString() ?? "").Replace("_TURRET", "") : "",
            ByMyTeam = victimTeam != myTeamId,
            KillerParticipantId = ev.TryGetProperty("killerId", out var k) ? k.GetInt32() : 0,
            X = ev.TryGetProperty("position", out var p) ? p.GetProperty("x").GetInt32() : 0,
            Y = ev.TryGetProperty("position", out var p2) ? p2.GetProperty("y").GetInt32() : 0,
        };
    }

    private static ObjectiveEvent ParseMonsterKill(JsonElement ev, int myTeamId)
    {
        var monsterType = ev.TryGetProperty("monsterType", out var mt) ? mt.GetString() ?? "" : "";
        var kind = monsterType switch
        {
            "DRAGON" => "DRAGON",
            "BARON_NASHOR" => "BARON",
            "RIFTHERALD" => "HERALD",
            "HORDE" => "GRUBS",
            "ATAKHAN" => "ATAKHAN",
            _ => monsterType,
        };
        return new ObjectiveEvent
        {
            TimeSec = TimeSecOf(ev),
            Kind = kind,
            SubKind = ev.TryGetProperty("monsterSubType", out var st) ? (st.GetString() ?? "").Replace("_DRAGON", "") : "",
            ByMyTeam = ev.TryGetProperty("killerTeamId", out var kt) && kt.GetInt32() == myTeamId,
            KillerParticipantId = ev.TryGetProperty("killerId", out var k) ? k.GetInt32() : 0,
            X = ev.TryGetProperty("position", out var p) ? p.GetProperty("x").GetInt32() : 0,
            Y = ev.TryGetProperty("position", out var p2) ? p2.GetProperty("y").GetInt32() : 0,
        };
    }

    /// Interpolate every player's position to the death timestamp and count who
    /// was actually near - the credited-kill list undercounts real collapses.
    private static void EnrichWithConvergence(Death death, List<Frame> frames, HashSet<int> enemyPids, HashSet<int> allyPids)
    {
        var before = frames.LastOrDefault(f => f.TimeSec <= death.TimeSec);
        var after = frames.FirstOrDefault(f => f.TimeSec > death.TimeSec);
        if (before is null && after is null) return;

        (int X, int Y)? PositionAt(int pid)
        {
            (int, int)? p0 = null, p1 = null;
            if (before is not null && before.Positions.TryGetValue(pid, out var b)) p0 = b;
            if (after is not null && after.Positions.TryGetValue(pid, out var a)) p1 = a;
            if (p0 is null || p1 is null) return p0 ?? p1;
            var span = after!.TimeSec - before!.TimeSec;
            var alpha = span <= 0 ? 0 : (double)(death.TimeSec - before.TimeSec) / span;
            return ((int)(p0.Value.Item1 + (p1.Value.Item1 - p0.Value.Item1) * alpha),
                    (int)(p0.Value.Item2 + (p1.Value.Item2 - p0.Value.Item2) * alpha));
        }

        static double Dist((int X, int Y) a, int x, int y) => Math.Sqrt(Math.Pow(a.X - x, 2) + Math.Pow(a.Y - y, 2));

        var enemiesNear = 0;
        foreach (var pid in enemyPids)
        {
            if (PositionAt(pid) is { } p && Dist(p, death.X, death.Y) <= NearRadius) enemiesNear++;
        }

        var alliesNear = 0;
        double? nearestAlly = null;
        foreach (var pid in allyPids)
        {
            if (PositionAt(pid) is not { } p) continue;
            var dist = Dist(p, death.X, death.Y);
            if (dist <= NearRadius) alliesNear++;
            if (nearestAlly is null || dist < nearestAlly) nearestAlly = dist;
        }

        death.EnemiesNearDeath = enemiesNear;
        death.AlliesNearDeath = alliesNear;
        death.NearestAllyDist = nearestAlly is { } d ? (int)d : null;
    }

    private static void EnrichWithObjectiveContext(Death death, List<ObjectiveEvent> objectives)
    {
        var lastFriendly = objectives
            .Where(o => o.ByMyTeam && o.Kind is not "TOWER" and not "INHIBITOR" && o.TimeSec <= death.TimeSec)
            .OrderByDescending(o => o.TimeSec)
            .FirstOrDefault();
        if (lastFriendly is not null && death.TimeSec - lastFriendly.TimeSec <= PostObjectiveWindowSec)
        {
            death.SecondsAfterObjective = death.TimeSec - lastFriendly.TimeSec;
            death.ObjectiveBefore = lastFriendly.Kind;
        }
    }

    private static (double? EnemyHalfPct, int? AvgNearestAllyDist) MovementMetrics(
        List<Frame> frames, MatchParticipantDto me, HashSet<int> allyPids)
    {
        // Skip the pre-minions frames - everyone idles in base and would dilute both metrics.
        var active = frames.Where(f => f.TimeSec >= 90).ToList();
        if (active is not { Count: > 0 }) return (null, null);

        var enemyHalfSamples = 0;
        var totalSamples = 0;
        var nearestDistances = new List<double>();

        foreach (var frame in active)
        {
            if (!frame.Positions.TryGetValue(me.ParticipantId, out var mine)) continue;
            totalSamples++;

            // Blue (100) owns the bottom-left triangle of the x+y diagonal.
            var inEnemyHalf = me.TeamId == 100 ? mine.X + mine.Y > MapDiagonal : mine.X + mine.Y < MapDiagonal;
            if (inEnemyHalf) enemyHalfSamples++;

            var nearest = allyPids
                .Select(pid => frame.Positions.TryGetValue(pid, out var p) ? (double?)Math.Sqrt(Math.Pow(p.X - mine.X, 2) + Math.Pow(p.Y - mine.Y, 2)) : null)
                .Where(d => d is not null)
                .Min();
            if (nearest is { } n) nearestDistances.Add(n);
        }

        return totalSamples == 0
            ? (null, null)
            : (Math.Round(100.0 * enemyHalfSamples / totalSamples, 1),
               nearestDistances is { Count: > 0 } ? (int)nearestDistances.Average() : null);
    }

    private static int TimeSecOf(JsonElement el) =>
        el.TryGetProperty("timestamp", out var ts) ? (int)(ts.GetInt64() / 1000) : 0;
}
