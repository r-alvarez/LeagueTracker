using System.Text.Json;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// Pids 1-5 are my team and 6-10 the enemy. I am pid 1 and touch no fight
// unless a test puts me on a ledger, so fights default to skipped ones - the
// kind that gets a camera.
public class TimelineAnalyzerFightsTests
{
    [Fact]
    public void Champions_on_a_victims_damage_ledger_count_toward_the_headcount()
    {
        var fight = new Scenario().Kill(70, killer: 6, victim: 2).Traded(7, 8, 3).OnlyFight();

        Assert.Equal((2, 3), (fight.Allies, fight.Enemies));
        Assert.Equal("skirmish", fight.Kind);
    }

    [Fact]
    public void Standing_near_the_fight_between_frames_no_longer_counts()
    {
        var fight = new Scenario().Near(3).Near(4).Near(8).Kill(70, killer: 6, victim: 2).OnlyFight();

        Assert.Equal((1, 1), (fight.Allies, fight.Enemies));
        Assert.Equal("duel", fight.Kind);
    }

    [Fact]
    public void Participation_is_read_off_the_ledger_not_off_proximity()
    {
        Assert.False(new Scenario().Near(1).Kill(70, killer: 6, victim: 2).OnlyFight().Participated);
        Assert.True(new Scenario().Kill(70, killer: 6, victim: 2).Traded(1).OnlyFight().Participated);
    }

    [Fact]
    public void Assisters_count_toward_the_headcount_wherever_the_frames_place_them()
    {
        var fight = new Scenario().Kill(70, killer: 6, victim: 2, 7, 8).OnlyFight();

        Assert.Equal(3, fight.Enemies);
        Assert.Equal("skirmish", fight.Kind);
    }

    [Fact]
    public void Survivor_on_the_kill_ledger_outranks_a_bystander_with_a_lower_pid()
    {
        var fight = new Scenario().Near(3).Kill(70, killer: 6, victim: 2, 7).OnlyFight();

        Assert.Equal(6, fight.CameraParticipantId);
    }

    [Fact]
    public void Ally_assister_who_survives_is_preferred_over_the_enemy_killer()
    {
        var fight = new Scenario().Kill(70, killer: 2, victim: 6, 3).Kill(72, killer: 7, victim: 2).OnlyFight();

        Assert.Equal(3, fight.CameraParticipantId);
    }

    [Fact]
    public void Kills_break_ties_before_assists()
    {
        var fight = new Scenario()
            .Kill(70, killer: 3, victim: 6, 2)
            .Kill(71, killer: 2, victim: 7, 3)
            .Kill(72, killer: 2, victim: 8)
            .OnlyFight();

        Assert.Equal(2, fight.CameraParticipantId);
    }

    [Fact]
    public void A_survivor_who_only_traded_blows_still_beats_the_last_victim_as_camera()
    {
        var fight = new Scenario().Kill(70, killer: 2, victim: 6).Traded(4).Kill(70, killer: 6, victim: 2).OnlyFight();

        Assert.Equal(4, fight.CameraParticipantId);
    }

    [Fact]
    public void Last_victim_films_when_nobody_on_the_ledger_survives()
    {
        var fight = new Scenario().Near(3).Kill(70, killer: 2, victim: 6).Kill(70, killer: 6, victim: 2).OnlyFight();

        Assert.Equal(2, fight.CameraParticipantId);
    }

    [Fact]
    public void Never_dead_survivor_outranks_one_who_may_still_be_on_a_respawn_timer()
    {
        var fights = new Scenario()
            .Kill(40, killer: 7, victim: 3)
            .Kill(70, killer: 2, victim: 6, 3, 4)
            .Kill(72, killer: 7, victim: 2)
            .Fights();

        Assert.Equal(4, fights.Single(f => f.StartSec == 70).CameraParticipantId);
    }

    private sealed class Scenario
    {
        private const int FightX = 7000;
        private const int FightY = 7000;

        private sealed record KillSpec(int Sec, int Killer, int Victim, int[] Assists)
        {
            public List<int> Traded { get; } = [];
        }

        private readonly Dictionary<int, (int X, int Y)> positions = Enumerable.Range(1, 10)
            .ToDictionary(pid => pid, pid => pid <= 5 ? (1000, 1000) : (13500, 13500));
        private readonly List<KillSpec> kills = [];

        public Scenario Near(int pid)
        {
            positions[pid] = (FightX, FightY);
            return this;
        }

        public Scenario Kill(int sec, int killer, int victim, params int[] assists)
        {
            kills.Add(new KillSpec(sec, killer, victim, assists));
            return this;
        }

        // Attaches to the most recent Kill - call it right after the kill it belongs to.
        public Scenario Traded(params int[] pids)
        {
            kills[^1].Traded.AddRange(pids);
            return this;
        }

        public TimelineAnalyzer.Fight OnlyFight() => Fights().Single();

        public List<TimelineAnalyzer.Fight> Fights()
        {
            var events = kills.Select(k => new
            {
                type = "CHAMPION_KILL",
                timestamp = k.Sec * 1000L,
                killerId = k.Killer,
                victimId = k.Victim,
                assistingParticipantIds = k.Assists,
                victimDamageReceived = k.Traded.Select(pid => new { type = "OTHER", participantId = pid, name = $"C{pid}" }).ToArray(),
                victimDamageDealt = new[] { new { type = "MINION", participantId = 0, name = "SRU_ChaosMinionMelee" } },
                position = new { x = FightX, y = FightY },
            }).ToArray();
            var frames = new[] { 0, 60, 120 }.Select(sec => new
            {
                timestamp = sec * 1000L,
                participantFrames = positions.ToDictionary(
                    kv => kv.Key.ToString(),
                    kv => new { position = new { x = kv.Value.X, y = kv.Value.Y }, totalGold = 0, currentGold = 0, xp = 0, level = 1, minionsKilled = 0, jungleMinionsKilled = 0 }),
                events = sec == 120 ? events : [],
            });
            var timeline = JsonSerializer.Serialize(new { info = new { frames } });

            var participants = Enumerable.Range(1, 10).Select(pid => new MatchParticipantDto
            {
                ParticipantId = pid,
                ChampionName = $"C{pid}",
                TeamId = pid <= 5 ? 100 : 200,
            }).ToList();
            var info = new MatchInfoDto { Participants = participants };
            return TimelineAnalyzer.Analyze(timeline, info, participants[0]).Fights;
        }
    }
}
