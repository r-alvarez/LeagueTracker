using LeagueTracker.RenderAgent;

namespace LeagueTracker.RenderAgent.Tests;

public class PostGameLpHiderTests
{
    [Theory]
    [InlineData("PreEndOfGame", true)]
    [InlineData("EndOfGame", true)]
    [InlineData("InProgress", false)]
    [InlineData("ChampSelect", false)]
    [InlineData("None", false)]
    [InlineData(null, false)]
    public void Only_the_screens_after_a_game_are_post_game(string? phase, bool expected) =>
        Assert.Equal(expected, PostGameLpHider.IsPostGame(phase));

    [Theory]
    [InlineData(420, true)]
    [InlineData(440, true)]
    [InlineData(-1, true)]
    [InlineData(400, false)]
    [InlineData(450, false)]
    public void Ranked_and_unknown_queues_move_lp(long queueId, bool expected) =>
        Assert.Equal(expected, PostGameLpHider.MovesLp(queueId));
}
