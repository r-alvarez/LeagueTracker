using LeagueTracker.Api.Data;

namespace LeagueTracker.Api.Services;

public sealed record RuleSpec(string Kind, Dictionary<string, int> Params);

public sealed record RuleResult(string Status, string Detail);

public sealed record RuleContext(
    Match Match, MatchParticipant Me, List<MatchParticipant> Participants,
    List<KillEvent> Kills, List<ObjectiveEvent> Objectives, List<PositionSample> Positions,
    List<ItemEvent> Items, List<TimelineAnalyzer.Fight> Fights, int[] LevelSecs, List<Death> Deaths)
{
    public int DurationSec => (int)Match.DurationSec;
    public MatchParticipant? AllyJungler => Participants.FirstOrDefault(p => p.IsAlly && p.Position is "JUNGLE");
    public HashSet<int> EnemyPids => Participants.Where(p => !p.IsAlly && !p.IsMe).Select(p => p.ParticipantId).ToHashSet();
    public HashSet<int> AllyPids => Participants.Where(p => p.IsAlly).Select(p => p.ParticipantId).ToHashSet();
    public List<PositionSample> PositionsOf(int pid) => Positions.Where(p => p.ParticipantId == pid).OrderBy(p => p.TimeSec).ToList();
}

// na = the game gave no opportunity; pending = the row predates LevelSecs.
public static class GameplanRules
{
    public const string Met = "met";
    public const string Missed = "missed";
    public const string NotApplicable = "na";
    public const string Pending = "pending";

    public static readonly string[] Phases = ["early", "mid", "late"];
    // Same boundaries the review verdicts draw.
    public const int EarlyEndSec = 840;
    public const int MidEndSec = 1500;

    private static readonly string[] EpicKinds = ["DRAGON", "BARON", "HERALD", "GRUBS", "ATAKHAN"];
    private const int SameCampSec = 90;
    private const int FightNearUnits = 2500;
    private const int ContestUnits = 2500;
    // Beyond this for the whole window, the jungler never came: n/a, not missed.
    private const int JunglerCameUnits = 4000;
    private const int TeamfightEnemies = 3;

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Defaults =
        new Dictionary<string, IReadOnlyDictionary<string, int>>
        {
            ["level_window_fight"] = new Dictionary<string, int> { ["level"] = 6, ["windowSec"] = 180, ["withJungler"] = 1 },
            // 60s frames cannot honestly resolve a lead shorter than a minute.
            ["objective_arrival"] = new Dictionary<string, int> { ["leadSec"] = 60, ["nearUnits"] = 4000, ["fromSec"] = EarlyEndSec, ["minPct"] = 67 },
            ["picks"] = new Dictionary<string, int> { ["minPicks"] = 1, ["fromSec"] = EarlyEndSec, ["isolationUnits"] = 2500 },
            ["item_by"] = new Dictionary<string, int> { ["itemId"] = 0, ["bySec"] = 480 },
            ["level_by"] = new Dictionary<string, int> { ["level"] = 9, ["bySec"] = 600 },
            ["jungler_proximity"] = new Dictionary<string, int> { ["fromSec"] = EarlyEndSec, ["toSec"] = MidEndSec, ["nearUnits"] = 3000, ["minPct"] = 40 },
            ["early_wards"] = new Dictionary<string, int> { ["minWards"] = 2 },
            ["caught_out"] = new Dictionary<string, int> { ["maxDeaths"] = 0, ["fromSec"] = EarlyEndSec },
            // One allowed: 0 / 1 / 2+ ran 61% / 55% / 27% win rate over 167 Viktor games.
            ["early_skirmish_deaths"] = new Dictionary<string, int> { ["maxDeaths"] = 1, ["includeGanks"] = 1, ["untilSec"] = EarlyEndSec },
        };

    public static RuleSpec? Normalize(RuleSpec? rule)
    {
        if (rule is null || !Defaults.TryGetValue(rule.Kind, out var defaults)) return null;
        var known = defaults.ToDictionary(kv => kv.Key, kv => rule.Params.TryGetValue(kv.Key, out var v) ? v : kv.Value);
        return new RuleSpec(rule.Kind, known);
    }

    public static int PhaseStartSec(string phase) => phase switch { "mid" => EarlyEndSec, "late" => MidEndSec, _ => 0 };

    public static RuleResult Evaluate(RuleSpec rule, RuleContext ctx)
    {
        var spec = Normalize(rule) ?? throw new ArgumentException($"unknown rule kind {rule.Kind}", nameof(rule));
        int P(string key) => spec.Params[key];
        return spec.Kind switch
        {
            "level_window_fight" => LevelWindowFight(ctx, P("level"), P("windowSec"), P("withJungler") != 0),
            "objective_arrival" => ObjectiveArrival(ctx, P("leadSec"), P("nearUnits"), P("fromSec"), P("minPct")),
            "picks" => Picks(ctx, P("minPicks"), P("fromSec"), P("isolationUnits")),
            "item_by" => ItemBy(ctx, P("itemId"), P("bySec")),
            "level_by" => LevelBy(ctx, P("level"), P("bySec")),
            "jungler_proximity" => JunglerProximity(ctx, P("fromSec"), P("toSec"), P("nearUnits"), P("minPct")),
            "early_wards" => EarlyWards(ctx, P("minWards")),
            "caught_out" => CaughtOut(ctx, P("maxDeaths"), P("fromSec")),
            "early_skirmish_deaths" => EarlySkirmishDeaths(ctx, P("maxDeaths"), P("includeGanks") != 0, P("untilSec")),
            _ => throw new ArgumentException($"unknown rule kind {spec.Kind}", nameof(rule)),
        };
    }

    // --- at level N, look for a fight with the jungler ------------------------------

    private static RuleResult LevelWindowFight(RuleContext ctx, int level, int windowSec, bool withJungler)
    {
        if (ctx.LevelSecs is not { Length: > 0 }) return new(Pending, "Level timings arrive with the next reprocess.");
        if (LevelSec(ctx, level) is not { } at) return new(NotApplicable, $"Never reached level {level}.");
        var end = Math.Min(at + windowSec, ctx.DurationSec);
        if (end - at < 30) return new(NotApplicable, $"Hit {level} at {Clock(at)} as the game ended.");

        var jungler = ctx.AllyJungler;
        if (withJungler && jungler is null) return new(NotApplicable, "No ally jungler in this game.");
        var junglerPositions = jungler is not null ? ctx.PositionsOf(jungler.ParticipantId) : [];

        var window = ctx.Fights.Where(f => f.Participated && f.StartSec >= at && f.StartSec <= end).ToList();
        var withHim = window.Where(f => !withJungler || JunglerInFight(ctx, f, jungler!.ParticipantId, junglerPositions)).ToList();
        var lead = $"Hit {level} at {Clock(at)}";
        if (withHim is { Count: > 0 })
        {
            var first = withHim[0];
            var more = withHim.Count > 1 ? $" (+{withHim.Count - 1} more)" : "";
            var who = withJungler ? $" with {jungler!.Champion}" : "";
            return new(Met, $"{lead} — {first.Kind} {first.Allies}v{first.Enemies}{who} at {Clock(first.StartSec)}, {first.Result}{more}.");
        }

        if (!withJungler) return new(Missed, $"{lead} — no fight of yours in the next {Clock(windowSec)}.");

        var (closest, when) = ClosestApproach(ctx.PositionsOf(ctx.Me.ParticipantId), junglerPositions, at, end);
        if (closest is null || closest > JunglerCameUnits)
        {
            return new(NotApplicable, $"{lead} — {jungler!.Champion} never came within {Units(JunglerCameUnits)} in the next {Clock(windowSec)}.");
        }
        var alone = window is { Count: > 0 } ? $" You fought {window.Count} time{(window.Count == 1 ? "" : "s")} without them." : "";
        return new(Missed, $"{lead} — {jungler!.Champion} was {Units(closest.Value)} from you at {Clock(when)} but no fight together followed.{alone}");
    }

    private static bool JunglerInFight(RuleContext ctx, TimelineAnalyzer.Fight fight, int junglerPid, List<PositionSample> junglerPositions)
    {
        var kills = ctx.Kills.Where(k => k.TimeSec >= fight.StartSec && k.TimeSec <= fight.EndSec).ToList();
        if (kills.Any(k => Ledger(k).Contains(junglerPid))) return true;
        if (kills is not { Count: > 0 }) return false;
        var mid = (fight.StartSec + fight.EndSec) / 2;
        return ReviewService.InterpolatedAt(junglerPositions, mid) is { } p
            && Dist(p, (int)kills.Average(k => k.X), (int)kills.Average(k => k.Y)) <= FightNearUnits;
    }

    // --- get to contested neutrals early ------------------------------------------------

    private static RuleResult ObjectiveArrival(RuleContext ctx, int leadSec, int nearUnits, int fromSec, int minPct)
    {
        List<ObjectiveEvent> epics = [];
        foreach (var o in ctx.Objectives.Where(o => EpicKinds.Contains(o.Kind) && o.TimeSec >= fromSec).OrderBy(o => o.TimeSec))
        {
            if (epics.Any(e => e.Kind == o.Kind && o.TimeSec - e.TimeSec <= SameCampSec)) continue;
            epics.Add(o);
        }

        var byPid = ctx.Positions.GroupBy(p => p.ParticipantId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.TimeSec).ToList());
        // Contest radius is fixed: tying it to the arrival knob changed which
        // objectives got judged at all, not just how strictly.
        int Near(IEnumerable<int> pids, ObjectiveEvent o, int atSec) => pids.Count(pid =>
            byPid.TryGetValue(pid, out var samples) && ReviewService.InterpolatedAt(samples, atSec) is { } p && Dist(p, o.X, o.Y) <= ContestUnits);

        var mine = ctx.PositionsOf(ctx.Me.ParticipantId);
        List<(ObjectiveEvent Epic, int? MyDist, bool Early)> contested = [];
        foreach (var epic in epics)
        {
            // A free take or a solo steal is nobody's arrival to judge.
            var us = Near(ctx.AllyPids.Append(ctx.Me.ParticipantId), epic, epic.TimeSec);
            var them = Near(ctx.EnemyPids, epic, epic.TimeSec);
            if (us < 2 || them < 1) continue;
            var myDist = ReviewService.InterpolatedAt(mine, epic.TimeSec - leadSec) is { } p ? (int?)Math.Round(Dist(p, epic.X, epic.Y)) : null;
            contested.Add((epic, myDist, myDist is { } d && d <= nearUnits));
        }

        if (contested is not { Count: > 0 }) return new(NotApplicable, "No contested neutral objective in this game.");
        var early = contested.Count(c => c.Early);
        var pct = (int)Math.Round(100.0 * early / contested.Count);
        var list = string.Join(", ", contested.Select(c =>
            $"{Clock(c.Epic.TimeSec)} {c.Epic.Kind.ToLowerInvariant()} {(c.Early ? "✓" : "✗")}{(c.MyDist is { } d ? $" ({Units(d)} away {leadSec}s before)" : "")}"));
        var status = pct >= minPct ? Met : Missed;
        return new(status, $"Early to {early} of {contested.Count} contested neutral{(contested.Count == 1 ? "" : "s")} — {list}.");
    }

    // --- picks: isolated kills we generated ---------------------------------------------

    private static RuleResult Picks(RuleContext ctx, int minPicks, int fromSec, int isolationUnits)
    {
        if (ctx.DurationSec < fromSec + 120) return new(NotApplicable, $"Game ended before {Clock(fromSec)}.");
        var enemies = ctx.EnemyPids;
        var byPid = ctx.Positions.GroupBy(p => p.ParticipantId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.TimeSec).ToList());
        var champByPid = ctx.Participants.ToDictionary(p => p.ParticipantId, p => p.Champion);

        List<string> picks = [];
        foreach (var k in ctx.Kills.Where(k => k.TimeSec >= fromSec && enemies.Contains(k.VictimParticipantId)).OrderBy(k => k.TimeSec))
        {
            if (!Ledger(k).Contains(ctx.Me.ParticipantId)) continue;
            if (ctx.Fights.Any(f => f.Enemies >= TeamfightEnemies && k.TimeSec >= f.StartSec && k.TimeSec <= f.EndSec)) continue;
            var company = enemies.Where(pid => pid != k.VictimParticipantId)
                .Select(pid => byPid.TryGetValue(pid, out var s) && ReviewService.InterpolatedAt(s, k.TimeSec) is { } p ? Dist(p, k.X, k.Y) : double.MaxValue)
                .DefaultIfEmpty(double.MaxValue).Min();
            if (company > isolationUnits)
            {
                var nearest = company < double.MaxValue ? $", nearest enemy {Units((int)company)} off" : "";
                picks.Add($"{Clock(k.TimeSec)} {champByPid.GetValueOrDefault(k.VictimParticipantId, "?")}{nearest}");
            }
        }

        return picks.Count >= minPicks
            ? new(Met, $"{picks.Count} pick{(picks.Count == 1 ? "" : "s")} after {Clock(fromSec)} — {string.Join("; ", picks)}.")
            : picks is { Count: > 0 }
                ? new(Missed, $"{picks.Count} pick{(picks.Count == 1 ? "" : "s")} after {Clock(fromSec)} (wanted {minPicks}) — {string.Join("; ", picks)}.")
                : new(Missed, $"No isolated kill after {Clock(fromSec)} — every kill you touched had enemy company within {Units(isolationUnits)}.");
    }

    // --- item / level by a time --------------------------------------------------------

    private static RuleResult ItemBy(RuleContext ctx, int itemId, int bySec)
    {
        if (itemId <= 0) return new(NotApplicable, "No item chosen for this point.");
        var bought = ctx.Items.Where(i => i.Kind is "PURCHASED" && i.ItemId == itemId).Select(i => (int?)i.TimeSec).Min();
        if (bought is null)
        {
            return ctx.DurationSec < bySec
                ? new(NotApplicable, $"Game ended before {Clock(bySec)}.")
                : new(Missed, "Never bought.");
        }
        return bought <= bySec
            ? new(Met, $"Bought at {Clock(bought.Value)} (target {Clock(bySec)}).")
            : new(Missed, $"Bought at {Clock(bought.Value)}, {Clock(bought.Value - bySec)} after the {Clock(bySec)} target.");
    }

    private static RuleResult LevelBy(RuleContext ctx, int level, int bySec)
    {
        if (ctx.LevelSecs is not { Length: > 0 }) return new(Pending, "Level timings arrive with the next reprocess.");
        if (LevelSec(ctx, level) is not { } at)
        {
            return ctx.DurationSec < bySec
                ? new(NotApplicable, $"Game ended before {Clock(bySec)}.")
                : new(Missed, $"Reached level {ctx.LevelSecs.Length}, not {level}, by {Clock(bySec)}.");
        }
        return at <= bySec
            ? new(Met, $"Level {level} at {Clock(at)} (target {Clock(bySec)}).")
            : new(Missed, $"Level {level} at {Clock(at)}, {Clock(at - bySec)} after the {Clock(bySec)} target.");
    }

    // --- share of the window spent near the jungler -------------------------------------

    private static RuleResult JunglerProximity(RuleContext ctx, int fromSec, int toSec, int nearUnits, int minPct)
    {
        if (ctx.AllyJungler is not { } jungler) return new(NotApplicable, "No ally jungler in this game.");
        // A game ending on a minute boundary files two frames at the same second.
        var theirs = ctx.PositionsOf(jungler.ParticipantId).GroupBy(p => p.TimeSec).ToDictionary(g => g.Key, g => g.First());
        var frames = ctx.PositionsOf(ctx.Me.ParticipantId)
            .Where(p => p.TimeSec >= fromSec && p.TimeSec <= toSec && theirs.ContainsKey(p.TimeSec))
            .DistinctBy(p => p.TimeSec)
            .ToList();
        if (frames.Count < 3) return new(NotApplicable, $"Game ended before {Clock(fromSec)} gave a window to judge.");
        var near = frames.Count(p => Dist((theirs[p.TimeSec].X, theirs[p.TimeSec].Y), p.X, p.Y) <= nearUnits);
        var pct = (int)Math.Round(100.0 * near / frames.Count);
        return new(pct >= minPct ? Met : Missed,
            $"Within {Units(nearUnits)} of {jungler.Champion} in {near} of {frames.Count} minutes between {Clock(fromSec)} and {Clock(Math.Min(toSec, frames[^1].TimeSec))} ({pct}%, wanted {minPct}%).");
    }

    // --- wards in the first ten minutes ---------------------------------------------------

    private static RuleResult EarlyWards(RuleContext ctx, int minWards)
    {
        if (ctx.DurationSec < 600) return new(NotApplicable, "Game ended before 10:00.");
        var wards = ctx.Match.WardsFirst10;
        var first = ctx.Match.FirstWardSec is { } sec ? $" (first at {Clock(sec)})" : "";
        return new(wards >= minWards ? Met : Missed,
            wards == 0 ? "No ward placed in the first 10 minutes." : $"{wards} ward{(wards == 1 ? "" : "s")} in the first 10 minutes{first}, wanted {minWards}.");
    }

    // --- caught alone after laning --------------------------------------------------------

    // The Discipline verdict's fog-pick test, so both agree on "caught".
    private static RuleResult CaughtOut(RuleContext ctx, int maxDeaths, int fromSec)
    {
        if (ctx.DurationSec < fromSec + 120) return new(NotApplicable, $"Game ended before {Clock(fromSec)}.");
        var caught = ctx.Deaths
            .Where(d => d is { EnemiesNearDeath: 0 } && d.TimeSec >= fromSec
                && !ctx.Fights.Any(f => f is { Participated: true, Enemies: >= TeamfightEnemies } && d.TimeSec >= f.StartSec && d.TimeSec <= f.EndSec))
            .OrderBy(d => d.TimeSec)
            .Select(d => $"{Clock(d.TimeSec)} by {d.KilledBy}{(d.Zone is { Length: > 0 } ? $" in {d.Zone.ToLowerInvariant()}" : "")}")
            .ToList();
        return caught.Count <= maxDeaths
            ? new(Met, caught is { Count: > 0 } ? $"Caught alone {caught.Count} time{(caught.Count == 1 ? "" : "s")} after {Clock(fromSec)} (allowed {maxDeaths}) — {string.Join("; ", caught)}."
                : $"Never caught alone after {Clock(fromSec)}.")
            : new(Missed, $"Caught alone {caught.Count} time{(caught.Count == 1 ? "" : "s")} after {Clock(fromSec)} — {string.Join("; ", caught)}.");
    }

    // --- careful in early skirmishes ------------------------------------------------------

    // A gank counts as outnumbered (1v2); a 1v1 death is a different failure.
    private static RuleResult EarlySkirmishDeaths(RuleContext ctx, int maxDeaths, bool includeGanks, int untilSec)
    {
        if (ctx.DurationSec < 300) return new(NotApplicable, "Game ended before laning.");
        var champByPid = ctx.Participants.ToDictionary(p => p.ParticipantId, p => p.Champion);
        List<string> outnumbered = [];
        foreach (var d in ctx.Deaths.Where(d => d.TimeSec < untilSec).OrderBy(d => d.TimeSec))
        {
            var fight = ctx.Fights.FirstOrDefault(f => f.Participated && d.TimeSec >= f.StartSec && d.TimeSec <= f.EndSec);
            var ganked = includeGanks && d.EnemyJunglerNear is true;
            if (fight is not { Enemies: >= 2 } && !ganked) continue;
            var how = ganked ? "ganked" : fight is not null ? $"{fight.Kind} {fight.Allies}v{fight.Enemies}" : "outnumbered";
            outnumbered.Add($"{Clock(d.TimeSec)} to {d.KilledBy} ({how})");
        }

        var early = ctx.Fights.Where(f => f.Participated && f.StartSec < untilSec && f.Kind is not "duel").ToList();
        var context = early is { Count: > 0 }
            ? $" Entered {early.Count} early skirmish{(early.Count == 1 ? "" : "es")}: won {early.Count(f => f.Result is "won")}, lost {early.Count(f => f.Result is "lost")}."
            : " No early skirmish entered.";
        var count = outnumbered.Count;
        var deaths = count == 0 ? $"No outnumbered death before {Clock(untilSec)}."
            : $"Died outnumbered {count} time{(count == 1 ? "" : "s")} before {Clock(untilSec)}{(count <= maxDeaths ? $" (allowed {maxDeaths})" : "")} — {string.Join("; ", outnumbered)}.";
        return new(count <= maxDeaths ? Met : Missed, deaths + context);
    }

    // --- shared ---------------------------------------------------------------------------

    private static HashSet<int> Ledger(KillEvent k)
    {
        var pids = new HashSet<int> { k.VictimParticipantId };
        if (k.KillerParticipantId > 0) pids.Add(k.KillerParticipantId);
        foreach (var csv in new[] { k.AssistIds, k.DamagePids })
        {
            foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out var pid)) pids.Add(pid);
            }
        }
        return pids;
    }

    private static (int? Dist, int AtSec) ClosestApproach(List<PositionSample> mine, List<PositionSample> theirs, int fromSec, int toSec)
    {
        var samples = mine.Select(p => p.TimeSec).Where(t => t > fromSec && t < toSec).Append(fromSec).Append(toSec).Distinct();
        int? best = null;
        var bestAt = fromSec;
        foreach (var sec in samples)
        {
            if (ReviewService.InterpolatedAt(mine, sec) is not { } me || ReviewService.InterpolatedAt(theirs, sec) is not { } jg) continue;
            var d = (int)Math.Round(Dist(me, jg.X, jg.Y));
            if (best is null || d < best) (best, bestAt) = (d, sec);
        }
        return (best, bestAt);
    }

    // LevelSecs is indexed from level 1 ("0,45,98" = levels 1, 2, 3).
    private static int? LevelSec(RuleContext ctx, int level) =>
        level >= 1 && level <= ctx.LevelSecs.Length ? ctx.LevelSecs[level - 1] : null;

    private static double Dist((int X, int Y) a, int x, int y) => Math.Sqrt(Math.Pow(a.X - x, 2) + Math.Pow(a.Y - y, 2));

    internal static string Clock(int sec) => $"{sec / 60}:{sec % 60:00}";

    private static string Units(int units) => units >= 1000 ? $"{units / 1000.0:0.#}k units" : $"{units} units";
}
