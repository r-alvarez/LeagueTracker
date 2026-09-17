using LeagueTracker.RenderAgent;

namespace LeagueTracker.RenderAgent.Tests;

public sealed class YouTubeChaptersAtUploadTests
{
    [Fact]
    public void A_description_listing_chapters_from_zero_has_chapters()
    {
        Assert.True(GameRecorder.HasChapters("Match EUW1_1\nReview on the tracker: https://t/x\n\n0:00 Loading screen\n5:05 Kill - Zed\n9:10 Death to Ahri"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Match EUW1_1")]
    [InlineData("Match EUW1_1\n10:00 Late start")]
    public void The_bare_match_line_or_no_zero_chapter_has_none(string? description)
    {
        Assert.False(GameRecorder.HasChapters(description));
    }
}
