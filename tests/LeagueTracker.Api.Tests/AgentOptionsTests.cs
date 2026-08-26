using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public class AgentOptionsTests
{
    private const string BenKeyId = "3f9c2a1b7d4e";

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
            [BenKeyId] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["YouTubeClientId"] = "ben-id",
                ["YouTubeRefreshToken"] = "",   // unset stack env var
            },
        },
    };

    [Fact]
    public void The_keyed_agent_gets_its_override_but_blank_values_do_not_replace_shared_ones()
    {
        var profile = Options().ProfileFor(BenKeyId.ToUpperInvariant());
        Assert.Equal("ben-id", profile["YouTubeClientId"]);
        Assert.Equal("shared-token", profile["YouTubeRefreshToken"]);
        Assert.Equal("ranked-solo", profile["RecordQueues"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a1b2c3d4e5f6")]
    [InlineData("DESKTOP-BEN")]   // a machine's name, not a key id (audit T-N1)
    public void Other_keys_get_the_shared_profile_untouched(string? agentId)
    {
        var profile = Options().ProfileFor(agentId);
        Assert.Equal("shared-id", profile["YouTubeClientId"]);
        Assert.Equal(3, profile.Count);
    }
}
