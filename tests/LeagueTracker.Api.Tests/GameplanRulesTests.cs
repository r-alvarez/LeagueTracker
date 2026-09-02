using LeagueTracker.Api.Data;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// Pids 1-5 are my team (1 = me in mid, 2 = our jungler ~4.9k away in the
// jungle), 6-10 the enemy (7 = their jungler). Frames every 60s, so
// placements must sit on a minute.
public class GameplanRulesTests
{
    private static RuleSpec Rule(string kind, params (string Key, int Value)[] overrides) =>
        new(kind, overrides.ToDictionary(o => o.Key, o => o.Value));

    // --- normalisation --------------------------------------------------------------

    [Fact]
    public void Normalize_fills_defaults_and_drops_keys_the_kind_does_not_know()
    {
        var spec = GameplanRules.Normalize(Rule("level_window_fight", ("windowSec", 240), ("bogus", 1)))!;

        Assert.Equal(6, spec.Params["level"]);
        Assert.Equal(240, spec.Params["windowSec"]);
        Assert.False(spec.Params.ContainsKey("bogus"));
    }

    [Fact]
    public void Normalize_returns_null_for_manual_and_unknown_kinds()
    {
        Assert.Null(GameplanRules.Normalize(null));
        Assert.Null(GameplanRules.Normalize(Rule("free_form_expression")));
    }

    // --- at 6, look for a 2v2 with the jungler ---------------------------------------

    [Fact]
    public void Level_window_fight_is_met_by_a_fight_with_the_jungler_on_the_ledger()
    {
        var ctx = new Scenario().Levels(0, 40, 90, 150, 220, 300, 400)
            .Kill(420, killer: 1, victim: 6, assists: 2)
            .Fight(420, 425, participated: true, allies: 2, enemies: 2)
            .Build();

        var result = GameplanRules.Evaluate(Rule("level_window_fight"), ctx);

        Assert.Equal(GameplanRules.Met, result.Status);
        Assert.Contains("Hit 6 at 5:00", result.Detail);
        Assert.Contains("with C2", result.Detail);
    }

    [Fact]
    public void Level_window_fight_declines_when_the_jungler_never_came()
    {
        var ctx = new Scenario().Levels(0, 40, 90, 150, 220, 300, 400).Build();

        var result = GameplanRules.Evaluate(Rule("level_window_fight"), ctx);

        Assert.Equal(GameplanRules.NotApplicable, result.Status);
        Assert.Contains("never came", result.Detail);
    }

    [Fact]
    public void Level_window_fight_is_missed_when_the_jungler_was_beside_me_and_nothing_happened()
    {
        var ctx = new Scenario().Levels(0, 40, 90, 150, 220, 300, 400).At(2, 480, 7000, 7000).At(1, 480, 7200, 7100).Build();

        var result = GameplanRules.Evaluate(Rule("level_window_fight"), ctx);

        Assert.Equal(GameplanRules.Missed, result.Status);
        Assert.Contains("no fight together", result.Detail);
    }

    [Fact]
    public void Level_window_fight_is_pending_until_level_timings_exist_and_na_when_the_level_never_came()
    {
        Assert.Equal(GameplanRules.Pending, GameplanRules.Evaluate(Rule("level_window_fight"), new Scenario().Build()).Status);
        Assert.Equal(GameplanRules.NotApplicable,
            GameplanRules.Evaluate(Rule("level_window_fight"), new Scenario().Levels(0, 40, 90).Build()).Status);
    }

    // --- picks -----------------------------------------------------------------------

    [Fact]
    public void Picks_count_isolated_kills_i_touched_after_laning_and_nothing_else()
    {
        var ctx = new Scenario()
            .Kill(900, killer: 1, victim: 6)                                   // isolated: counts
            .Kill(1000, killer: 3, victim: 8)                                  // not mine: skipped
            .Kill(1100, killer: 1, victim: 9).At(10, 1080, 7000, 7000).At(10, 1140, 7000, 7000)   // an enemy stood with the victim: skipped
            .Kill(1200, killer: 1, victim: 6).Fight(1195, 1210, participated: true, allies: 4, enemies: 4) // teamfight: skipped
            .Kill(600, killer: 1, victim: 7)                                   // laning phase: skipped
            .Build();

        var result = GameplanRules.Evaluate(Rule("picks"), ctx);

        Assert.Equal(GameplanRules.Met, result.Status);
        Assert.StartsWith("1 pick after 14:00 — 15:00 C6", result.Detail);
    }

    [Fact]
    public void Picks_declines_when_the_game_ended_in_laning()
        => Assert.Equal(GameplanRules.NotApplicable, GameplanRules.Evaluate(Rule("picks"), new Scenario().Duration(700).Build()).Status);

    // --- item / level by ------------------------------------------------------------------

    [Fact]
    public void Item_by_reads_the_first_purchase_against_the_target()
    {
        var early = new Scenario().Bought(3802, 450).Bought(3802, 900).Build();
        var late = new Scenario().Bought(3802, 552).Build();
        var never = new Scenario().Build();
        var shortGame = new Scenario().Duration(400).Build();

        Assert.Equal(GameplanRules.Met, GameplanRules.Evaluate(Rule("item_by", ("itemId", 3802)), early).Status);
        var lateResult = GameplanRules.Evaluate(Rule("item_by", ("itemId", 3802)), late);
        Assert.Equal(GameplanRules.Missed, lateResult.Status);
        Assert.Equal("Bought at 9:12, 1:12 after the 8:00 target.", lateResult.Detail);
        Assert.Equal(GameplanRules.Missed, GameplanRules.Evaluate(Rule("item_by", ("itemId", 3802)), never).Status);
        Assert.Equal(GameplanRules.NotApplicable, GameplanRules.Evaluate(Rule("item_by", ("itemId", 3802)), shortGame).Status);
        Assert.Equal(GameplanRules.NotApplicable, GameplanRules.Evaluate(Rule("item_by"), early).Status);
    }

    [Fact]
    public void Level_by_compares_my_own_clock_to_the_target()
    {
        var ctx = new Scenario().Levels(0, 40, 90, 150, 220, 300, 400, 480, 570).Build();

        Assert.Equal(GameplanRules.Met, GameplanRules.Evaluate(Rule("level_by", ("level", 9), ("bySec", 600)), ctx).Status);
        Assert.Equal(GameplanRules.Missed, GameplanRules.Evaluate(Rule("level_by", ("level", 9), ("bySec", 540)), ctx).Status);
        Assert.Equal(GameplanRules.Missed, GameplanRules.Evaluate(Rule("level_by", ("level", 11), ("bySec", 600)), ctx).Status);
    }

    // --- jungler proximity ------------------------------------------------------------------

    [Fact]
    public void Jungler_proximity_is_the_share_of_window_minutes_spent_near_them()
    {
        var scenario = new Scenario().Duration(1560);
        foreach (var sec in new[] { 840, 900, 960, 1020, 1080, 1140 }) scenario.At(1, sec, 7000, 7000);
        foreach (var sec in new[] { 840, 900, 960 }) scenario.At(2, sec, 7500, 7500);   // near for 3 of 6

        var result = GameplanRules.Evaluate(Rule("jungler_proximity", ("toSec", 1140)), scenario.Build());

        Assert.Equal(GameplanRules.Met, result.Status);
        Assert.Contains("3 of 6 minutes", result.Detail);
        Assert.Contains("(50%, wanted 40%)", result.Detail);
    }

    // --- contested neutrals -------------------------------------------------------------------

    [Fact]
    public void Objective_arrival_judges_only_contested_takes_and_reads_my_distance_before_them()
    {
        var pit = (X: 9800, Y: 4400);
        var ctx = new Scenario()
            .Objective("DRAGON", 1200, pit.X, pit.Y, byMyTeam: true)
            .At(1, 1140, pit.X - 500, pit.Y).At(1, 1200, pit.X, pit.Y)        // I was there a minute early
            .At(2, 1200, pit.X, pit.Y).At(7, 1200, pit.X + 300, pit.Y)          // both junglers at the pit = contested
            .Objective("HERALD", 1500, 5000, 10000, byMyTeam: false)             // nobody of mine near = a free take, not judged
            .Build();

        var result = GameplanRules.Evaluate(Rule("objective_arrival"), ctx);

        Assert.Equal(GameplanRules.Met, result.Status);
        Assert.StartsWith("Early to 1 of 1 contested neutral — 20:00 dragon ✓", result.Detail);
    }

    // --- wards / caught out -------------------------------------------------------------------

    [Fact]
    public void Early_wards_reads_the_first_ten_minutes_off_the_match_row()
    {
        var two = GameplanRules.Evaluate(Rule("early_wards"), new Scenario().Wards(2, firstSec: 84).Build());
        var none = GameplanRules.Evaluate(Rule("early_wards"), new Scenario().Wards(0).Build());

        Assert.Equal(GameplanRules.Met, two.Status);
        Assert.Equal("2 wards in the first 10 minutes (first at 1:24), wanted 2.", two.Detail);
        Assert.Equal(GameplanRules.Missed, none.Status);
        Assert.Equal(GameplanRules.NotApplicable, GameplanRules.Evaluate(Rule("early_wards"), new Scenario().Duration(500).Build()).Status);
    }

    [Fact]
    public void Caught_out_counts_nobody_near_deaths_after_laning_outside_committed_fights()
    {
        var ctx = new Scenario()
            .Died(700, enemiesNear: 0, killedBy: "Zed")                        // laning: not counted
            .Died(1000, enemiesNear: 0, killedBy: "Khazix", zone: "Bot river")   // caught
            .Died(1300, enemiesNear: 0, killedBy: "Jhin").Fight(1290, 1310, participated: true, allies: 4, enemies: 4)   // teamfight
            .Died(1500, enemiesNear: 2, killedBy: "Zed")                       // they were on screen
            .Build();

        var result = GameplanRules.Evaluate(Rule("caught_out"), ctx);

        Assert.Equal(GameplanRules.Missed, result.Status);
        Assert.Equal("Caught alone 1 time after 14:00 — 16:40 by Khazix in bot river.", result.Detail);
        Assert.Equal(GameplanRules.Met, GameplanRules.Evaluate(Rule("caught_out", ("maxDeaths", 1)), ctx).Status);
    }

    [Fact]
    public void Early_skirmish_deaths_count_outnumbered_deaths_before_laning_ends_and_allow_one_by_default()
    {
        var ctx = new Scenario()
            .Died(300, enemiesNear: 1, killedBy: "Zed").Fight(295, 300, participated: true, allies: 1, enemies: 1, kind: "duel")   // 1v1: not counted
            .Died(500, enemiesNear: 2, killedBy: "LeeSin", junglerNear: true)                                                 // gank
            .Died(700, enemiesNear: 2, killedBy: "Zed").Fight(690, 705, participated: true, allies: 2, enemies: 2, kind: "skirmish", result: "lost")
            .Died(900, enemiesNear: 3, killedBy: "Zed").Fight(895, 905, participated: true, allies: 3, enemies: 3)            // after laning
            .Build();

        var result = GameplanRules.Evaluate(Rule("early_skirmish_deaths"), ctx);

        Assert.Equal(GameplanRules.Missed, result.Status);
        Assert.Equal("Died outnumbered 2 times before 14:00 — 8:20 to LeeSin (ganked); 11:40 to Zed (skirmish 2v2). Entered 1 early skirmish: won 0, lost 1.", result.Detail);
        Assert.Equal(GameplanRules.Met, GameplanRules.Evaluate(Rule("early_skirmish_deaths", ("includeGanks", 0)), ctx).Status);
    }

    // --- phase gating in the service ----------------------------------------------------------

    [Fact]
    public void A_phase_the_game_never_reached_answers_na_even_for_manual_points()
    {
        var plan = new Gameplan("C1", [
            new ReferencePoint("a1", "early", "Trade off last hits", null),
            new ReferencePoint("b2", "late", "Play off R", null),
        ], DateTime.UtcNow);
        var checks = new MatchChecks("M", new() { ["a1"] = new PointRating("met", null, DateTime.UtcNow) });

        var result = GameplanService.Evaluate(new Scenario().Duration(1000).Build(), plan, checks);

        Assert.Equal("met", result.Points[0].Status);
        Assert.Null(result.Points[0].Auto);
        Assert.Equal("na", result.Points[1].Status);
        Assert.Equal(1, result.Summary["met"]);
    }

    private sealed class Scenario
    {
        private readonly Dictionary<(int Pid, int Sec), (int X, int Y)> placed = [];
        private readonly List<KillEvent> kills = [];
        private readonly List<ObjectiveEvent> objectives = [];
        private readonly List<ItemEvent> items = [];
        private readonly List<TimelineAnalyzer.Fight> fights = [];
        private readonly List<Death> deaths = [];
        private int[] levelSecs = [];
        private int durationSec = 1800;
        private int wardsFirst10;
        private int? firstWardSec;

        public Scenario Duration(int sec) { durationSec = sec; return this; }
        public Scenario Levels(params int[] secs) { levelSecs = secs; return this; }
        public Scenario Wards(int first10, int? firstSec = null) { wardsFirst10 = first10; firstWardSec = firstSec; return this; }

        public Scenario Died(int sec, int enemiesNear, string killedBy, string zone = "", bool junglerNear = false)
        {
            deaths.Add(new Death
            {
                TimeSec = sec, EnemiesNearDeath = enemiesNear, KilledBy = killedBy, Zone = zone, X = 7000, Y = 7000,
                EnemyJunglerNear = junglerNear,
            });
            return this;
        }
        public Scenario At(int pid, int sec, int x, int y) { placed[(pid, sec)] = (x, y); return this; }
        public Scenario Bought(int itemId, int sec) { items.Add(new ItemEvent { TimeSec = sec, Kind = "PURCHASED", ItemId = itemId }); return this; }

        public Scenario Kill(int sec, int killer, int victim, params int[] assists)
        {
            kills.Add(new KillEvent
            {
                TimeSec = sec, KillerParticipantId = killer, VictimParticipantId = victim,
                AssistIds = string.Join(',', assists), X = 7000, Y = 7000,
            });
            return this;
        }

        public Scenario Fight(int start, int end, bool participated, int allies, int enemies, string kind = "skirmish", string result = "won")
        {
            fights.Add(new TimelineAnalyzer.Fight(start, end, kind, result, participated, allies, enemies, 1, 0, 0, false));
            return this;
        }

        public Scenario Objective(string kind, int sec, int x, int y, bool byMyTeam)
        {
            objectives.Add(new ObjectiveEvent { TimeSec = sec, Kind = kind, X = x, Y = y, ByMyTeam = byMyTeam });
            return this;
        }

        public RuleContext Build()
        {
            var participants = Enumerable.Range(1, 10).Select(pid => new MatchParticipant
            {
                ParticipantId = pid, Champion = $"C{pid}", TeamId = pid <= 5 ? 100 : 200,
                IsMe = pid == 1, IsAlly = pid is > 1 and <= 5,
                Position = pid is 2 or 7 ? "JUNGLE" : pid is 1 or 6 ? "MIDDLE" : "TOP",
            }).ToList();

            List<PositionSample> positions = [];
            for (var sec = 0; sec <= durationSec; sec += 60)
            {
                foreach (var pid in Enumerable.Range(1, 10))
                {
                    var (x, y) = placed.TryGetValue((pid, sec), out var p) ? p
                        : pid == 1 ? (7000, 7000)
                        : pid == 2 ? (2500, 9000)
                        : pid <= 5 ? (1000, 1000)
                        : (13500, 13500);
                    positions.Add(new PositionSample { ParticipantId = pid, TimeSec = sec, X = x, Y = y });
                }
            }

            var match = new Match
            {
                Id = "M", Champion = "C1", DurationSec = durationSec, HasTimeline = true,
                WardsFirst10 = wardsFirst10, FirstWardSec = firstWardSec,
            };
            return new RuleContext(match, participants[0], participants, kills, objectives, positions, items, fights, levelSecs, deaths);
        }
    }
}
