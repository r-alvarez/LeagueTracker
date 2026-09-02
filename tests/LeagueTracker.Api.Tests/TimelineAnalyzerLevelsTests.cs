using System.Text.Json;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// I am pid 1.
public class TimelineAnalyzerLevelsTests
{
    [Fact]
    public void Level_seconds_start_at_zero_for_level_one_and_stop_at_the_first_level_never_reached()
    {
        var analysis = Analyze(
            LevelUp(1, 2, 45), LevelUp(1, 3, 98), LevelUp(1, 5, 300),   // level 4 missing -> list ends at 3
            LevelUp(2, 2, 40));                                        // someone else's level-ups never mix in

        Assert.Equal("0,45,98", analysis.LevelSecs);
    }

    [Fact]
    public void Duplicate_skill_ups_at_the_same_millisecond_count_once()
    {
        var analysis = Analyze(
            SkillUp(1, 1, 5000), SkillUp(1, 1, 5000),   // the #1100 duplicate
            SkillUp(1, 2, 60000),
            SkillUp(1, 1, 120000), SkillUp(1, 1, 120001)); // a real double rank-up is two clicks apart

        Assert.Equal("1,2,1,1", analysis.SkillOrder);
    }

    private static object LevelUp(int pid, int level, int sec) =>
        new { type = "LEVEL_UP", timestamp = sec * 1000L, participantId = pid, level };

    private static object SkillUp(int pid, int slot, long ms) =>
        new { type = "SKILL_LEVEL_UP", timestamp = ms, participantId = pid, skillSlot = slot, levelUpType = "NORMAL" };

    private static TimelineAnalysis Analyze(params object[] events)
    {
        var participantFrames = Enumerable.Range(1, 10).ToDictionary(
            pid => pid.ToString(),
            pid => new { position = new { x = 1000, y = 1000 }, totalGold = 0, currentGold = 0, xp = 0, level = 1, minionsKilled = 0, jungleMinionsKilled = 0 });
        var frames = new[]
        {
            new { timestamp = 0L, participantFrames, events = (object[])[] },
            new { timestamp = 600_000L, participantFrames, events },
        };
        var timeline = JsonSerializer.Serialize(new { info = new { frames } });
        var participants = Enumerable.Range(1, 10)
            .Select(pid => new MatchParticipantDto { ParticipantId = pid, ChampionName = $"C{pid}", TeamId = pid <= 5 ? 100 : 200 })
            .ToList();
        return TimelineAnalyzer.Analyze(timeline, new MatchInfoDto { Participants = participants }, participants[0]);
    }
}
