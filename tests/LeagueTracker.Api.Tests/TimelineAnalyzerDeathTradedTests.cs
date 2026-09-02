using LeagueTracker.Api.Data;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// One payment test serves both follow-ins and outnumbered steps, so its
// edges are pinned: the kill window closes 10s after the death, buildings
// count from 30s before the trigger, and only within 3500 units.
public class TimelineAnalyzerDeathTradedTests
{
    private const int Trigger = 1278;
    private static readonly Death Fallen = new() { TimeSec = 1293, X = 9767, Y = 9619 };
    private static bool IsEnemy(int pid) => pid > 5;

    private static KillEvent Kill(int sec, int victim) => new() { TimeSec = sec, VictimParticipantId = victim };
    private static ObjectiveEvent Building(int sec, bool mine, int x, int y) =>
        new() { TimeSec = sec, Kind = "TOWER", ByMyTeam = mine, X = x, Y = y };

    [Fact]
    public void An_enemy_falling_after_the_trigger_pays()
        => Assert.True(TimelineAnalyzer.DeathTraded(Trigger, Fallen, [Kill(1285, 7)], [], IsEnemy));

    [Fact]
    public void An_enemy_falling_ten_seconds_after_the_death_still_pays()
        => Assert.True(TimelineAnalyzer.DeathTraded(Trigger, Fallen, [Kill(1303, 7)], [], IsEnemy));

    [Fact]
    public void An_enemy_falling_later_than_that_does_not_pay()
        => Assert.False(TimelineAnalyzer.DeathTraded(Trigger, Fallen, [Kill(1304, 7)], [], IsEnemy));

    [Fact]
    public void An_enemy_falling_before_the_trigger_does_not_pay()
        => Assert.False(TimelineAnalyzer.DeathTraded(Trigger, Fallen, [Kill(1277, 7)], [], IsEnemy));

    [Fact]
    public void An_ally_falling_is_not_payment()
        => Assert.False(TimelineAnalyzer.DeathTraded(Trigger, Fallen, [Kill(1290, 3)], [], IsEnemy));

    [Fact]
    public void My_team_taking_the_inhibitor_tower_at_the_spot_pays()
        => Assert.True(TimelineAnalyzer.DeathTraded(Trigger, Fallen, [], [Building(1300, true, 11134, 11207)], IsEnemy));

    [Fact]
    public void A_building_taken_a_lane_over_does_not_pay()
        => Assert.False(TimelineAnalyzer.DeathTraded(Trigger, Fallen, [], [Building(1300, true, 13604, 8691)], IsEnemy));

    [Fact]
    public void The_enemy_taking_a_building_is_not_payment()
        => Assert.False(TimelineAnalyzer.DeathTraded(Trigger, Fallen, [], [Building(1300, false, 11134, 11207)], IsEnemy));
}
