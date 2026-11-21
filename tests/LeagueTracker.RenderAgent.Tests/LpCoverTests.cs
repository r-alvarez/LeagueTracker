using System.Drawing;
using LeagueTracker.RenderAgent;

namespace LeagueTracker.RenderAgent.Tests;

public class LpCoverTests
{
    [Theory]
    [InlineData("PreEndOfGame", 420)]
    [InlineData("EndOfGame", 440)]
    [InlineData("EndOfGame", -1)]
    public void Ranked_post_game_screens_are_covered(string phase, long queueId) =>
        Assert.True(LpCover.ShouldCover(phase, queueId, hideLp: true));

    [Theory]
    [InlineData("EndOfGame", 420, false)]
    [InlineData("EndOfGame", 400, true)]
    [InlineData("InProgress", 420, true)]
    [InlineData("ChampSelect", 420, true)]
    [InlineData("None", 420, true)]
    public void Nothing_is_covered_without_the_setting_a_ranked_queue_or_a_post_game_screen(string phase, long queueId, bool hideLp) =>
        Assert.False(LpCover.ShouldCover(phase, queueId, hideLp));

    [Fact]
    public void A_dragged_cover_lands_in_the_same_place_on_a_resized_client()
    {
        var small = (X: 100, Y: 50, Width: 1280, Height: 720);
        var placement = CoverRect.Of(new Rectangle(100 + 320, 50 + 360, 256, 144), small);

        var large = (X: 0, Y: 0, Width: 1600, Height: 900);
        Assert.Equal(new Rectangle(400, 450, 320, 180), placement.Within(large));
    }
}
