using LeagueTracker.RenderAgent;

namespace LeagueTracker.RenderAgent.Tests;

public class AgentConfigProfileTests
{
    private static Dictionary<string, string> Profile(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void An_allowed_key_applies_and_is_reported()
    {
        var config = new AgentConfig();
        var applied = config.ApplyProfile(Profile(("YouTubeClientId", "client-1"), ("recordnameprefix", "Road to Platinum"), ("UploadVods", "false")));

        Assert.Equal("client-1", config.YouTubeClientId);
        Assert.Equal("Road to Platinum", config.RecordNamePrefix);
        Assert.False(config.UploadVods);
        Assert.Equal(["YouTubeClientId", "RecordNamePrefix", "UploadVods"], applied);
    }

    [Theory]
    [InlineData("RecordingsDir", @"\\evil\share")]
    [InlineData("RecordScratchDir", @"\\evil\share")]
    [InlineData("FfmpegPath", @"C:\anything.exe")]
    [InlineData("ServerUrl", "http://elsewhere")]
    [InlineData("PostGameReview", "true")]
    [InlineData("AutoLaunchClient", "true")]
    [InlineData("RecordInputs", "true")]
    [InlineData("RecordAudio", "true")]
    [InlineData("RecordQueues", "all")]
    [InlineData("YouTubeVisibility", "public")]
    [InlineData("MaxRecordingsGb", "0.001")]
    [InlineData("KeepRecordingsAfterPublish", "false")]
    [InlineData("RecordGames", "false")]
    [InlineData("JoinCode", "ABCD1234")]
    public void A_key_outside_the_allow_list_is_ignored(string key, string value)
    {
        var config = new AgentConfig { RecordInputs = false, RecordAudio = false, PostGameReview = false, AutoLaunchClient = false };
        var before = Snapshot(config);

        var applied = config.ApplyProfile(Profile((key, value)));

        Assert.Empty(applied);
        Assert.Equal(before, Snapshot(config));
    }

    [Fact]
    public void An_overflowing_int_is_skipped_instead_of_throwing()
    {
        var config = new AgentConfig();
        var applied = config.ApplyProfile(Profile(("RecordFramerate", "99999999999"), ("PollSeconds", "not a number"), ("UploadInGameMbps", "1e400x")));

        Assert.Empty(applied);
        Assert.Equal(60, config.RecordFramerate);
        Assert.Equal(60, config.PollSeconds);
        Assert.Equal(0, config.UploadInGameMbps);
    }

    [Fact]
    public void A_value_equal_to_the_current_one_is_not_reported_as_applied()
    {
        var config = new AgentConfig();
        Assert.Empty(config.ApplyProfile(Profile(("RecordFramerate", "60"), ("HdrToneMap", "true"))));
    }

    private static string Snapshot(AgentConfig config) =>
        string.Join("|", typeof(AgentConfig).GetProperties().Where(p => p.CanWrite).Select(p => $"{p.Name}={p.GetValue(config)}"));
}
