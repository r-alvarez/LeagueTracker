using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public sealed class YouTubeChaptersTests
{
    private static ReviewReelService.Moment Moment(int startSec, string title, string detail = "") =>
        new("fight", startSec + 20, startSec, startSec + 40, title, detail);

    private static ReviewReelService.Reel Reel(params ReviewReelService.Moment[] moments) => new("EUW1_1", "Me#EUW", "Ahri", moments);

    [Fact]
    public void Chapters_sit_on_the_recording_clock_not_the_game_clock()
    {
        var clockMap = new[] { (75.0, 0.0), (675.0, 600.0) };
        var text = YouTubeChapters.Describe(Reel(Moment(100, "Skirmish 2v1", "1-0 · +300g"), Moment(400, "Death to Orianna")), clockMap, "https://t/euw/Me/matches/EUW1_1");

        Assert.NotNull(text);
        var lines = text.Split('\n');
        Assert.Equal("Match EUW1_1", lines[0]);
        Assert.Equal("Review on the tracker: https://t/euw/Me/matches/EUW1_1", lines[1]);
        Assert.Contains("0:00 Loading screen", lines);
        Assert.Contains("2:55 Skirmish 2v1 - 1-0 · +300g", lines);
        Assert.Contains("7:55 Death to Orianna", lines);
    }

    [Fact]
    public void A_seam_shifts_only_the_moments_after_it()
    {
        var clockMap = new[] { (0.0, 0.0), (300.0, 300.0), (301.0, 331.0), (600.0, 630.0) };
        Assert.Equal(200, YouTubeChapters.VideoFor(clockMap, 200));
        Assert.Equal(370, YouTubeChapters.VideoFor(clockMap, 400));
    }

    [Fact]
    public void Fewer_than_three_chapters_is_no_description()
    {
        Assert.Null(YouTubeChapters.Describe(Reel(Moment(100, "Kill - Zed")), [], null));
    }

    [Fact]
    public void Moments_closer_than_ten_seconds_fold_into_the_earlier_one()
    {
        var text = YouTubeChapters.Describe(Reel(Moment(5, "Kill - Zed"), Moment(100, "Skirmish 2v1"), Moment(105, "Death to Zed"), Moment(300, "Teamfight 5v5")), [], null);

        Assert.NotNull(text);
        Assert.DoesNotContain("Kill - Zed", text);
        Assert.DoesNotContain("Death to Zed", text);
        Assert.Contains("1:40 Skirmish 2v1", text);
        Assert.Contains("5:00 Teamfight 5v5", text);
    }

    [Fact]
    public void Clocks_past_an_hour_carry_the_hour()
    {
        Assert.Equal("1:02:03", YouTubeChapters.Clock(3723));
        Assert.Equal("0:07", YouTubeChapters.Clock(7.9));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=PIqINVBRrO0", "PIqINVBRrO0")]
    [InlineData("https://youtu.be/q7vvFZvlkl8?t=10", "q7vvFZvlkl8")]
    [InlineData("https://www.youtube.com/shorts/abcdefghijk", "abcdefghijk")]
    [InlineData("https://example.com/x", null)]
    public void Video_ids_come_out_of_every_link_shape(string url, string? id) =>
        Assert.Equal(id, YouTubeChapterService.VideoIdOf(url));
}
