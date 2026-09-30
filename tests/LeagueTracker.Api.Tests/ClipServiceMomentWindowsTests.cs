using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public class ClipServiceMomentWindowsTests
{
    private const int Me = 1;
    private const double GameLength = 1800;

    private static ClipService.Kill Kill(int time, int killer, int victim, int[]? assists = null, int[]? damage = null, int x = 5000, int y = 5000) =>
        new(time, killer, victim, x, y, assists ?? [], damage ?? []);

    private static List<ClipWindow> Plan(params ClipService.Kill[] kills) => ClipService.MomentWindows([.. kills], Me, GameLength);

    [Fact]
    public void An_assists_only_game_still_gets_its_fights_clipped()
    {
        var windows = Plan(Kill(164, 2, 7, assists: [Me]), Kill(353, 3, 8, assists: [Me, 2]));

        Assert.Equal(new[] { "assist", "assist" }, windows.Select(w => w.Label).ToArray());
        Assert.All(windows, w => Assert.Equal("moment", w.Kind));
    }

    [Fact]
    public void Trading_blows_with_the_victim_counts_as_being_in_the_fight()
    {
        var window = Assert.Single(Plan(Kill(600, 2, 7, damage: [Me])));

        Assert.Equal("trade", window.Label);
        Assert.Equal("trade", Assert.Single(window.Events).Kind);
    }

    [Fact]
    public void Kills_the_player_had_no_part_in_get_no_window()
    {
        Assert.Empty(Plan(Kill(600, 2, 7, assists: [3]), Kill(900, 8, 4)));
    }

    [Fact]
    public void Assists_join_the_players_kill_in_one_window_labelled_by_the_kill()
    {
        var window = Assert.Single(Plan(Kill(600, 2, 7, assists: [Me]), Kill(610, Me, 8)));

        Assert.Equal("kill", window.Label);
        Assert.Equal(new[] { "assist", "kill" }, window.Events.Select(e => e.Kind).ToArray());
        Assert.Equal((580, 620), (window.StartSec, window.EndSec));
    }

    [Fact]
    public void A_death_outranks_assists_in_the_label()
    {
        Assert.Equal("death", Assert.Single(Plan(Kill(600, 2, 7, assists: [Me]), Kill(605, 8, Me))).Label);
        Assert.Equal("fight", Assert.Single(Plan(Kill(600, Me, 7), Kill(605, 8, Me))).Label);
    }

    [Fact]
    public void Several_assists_in_one_play_are_counted_in_the_label()
    {
        Assert.Equal("3-assists", Assert.Single(Plan(Kill(600, 2, 7, assists: [Me]), Kill(605, 2, 8, assists: [Me]), Kill(612, 3, 9, assists: [Me]))).Label);
    }
}
