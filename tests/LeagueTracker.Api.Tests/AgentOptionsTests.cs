using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public class AgentOptionsTests
{
    private static AgentOptions Options() => new()
    {
        Profile = new(StringComparer.OrdinalIgnoreCase)
        {
            ["YouTubeClientId"] = "shared-id",
            ["YouTubeRefreshToken"] = "shared-token",
            ["RecordQueues"] = "ranked-solo",
        },
        Profiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DESKTOP-BEN"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["YouTubeClientId"] = "ben-id",
                ["YouTubeRefreshToken"] = "",   // unset stack env var
            },
        },
    };

    [Fact]
    public void Named_agent_gets_its_override_but_blank_values_do_not_replace_shared_ones()
    {
        var profile = Options().ProfileFor("desktop-ben");
        Assert.Equal("ben-id", profile["YouTubeClientId"]);
        Assert.Equal("shared-token", profile["YouTubeRefreshToken"]);
        Assert.Equal("ranked-solo", profile["RecordQueues"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RUBEN")]
    public void Other_agents_get_the_shared_profile_untouched(string? agent)
    {
        var profile = Options().ProfileFor(agent);
        Assert.Equal("shared-id", profile["YouTubeClientId"]);
        Assert.Equal(3, profile.Count);
    }
}
