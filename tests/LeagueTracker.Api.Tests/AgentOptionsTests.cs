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
            ["YouTubeClientSecret"] = "shared-secret",
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
    public void An_incomplete_keyed_credential_set_falls_back_as_a_group()
    {
        var profile = Options().ProfileFor(BenKeyId.ToUpperInvariant());
        Assert.Equal("shared-id", profile["YouTubeClientId"]);
        Assert.Equal("shared-secret", profile["YouTubeClientSecret"]);
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
        Assert.Equal(4, profile.Count);
    }

    [Fact]
    public void A_friends_machine_loses_the_shared_secrets_but_uploads_go_off()
    {
        var options = Options();
        options.Profile["YouTubeUpload"] = "true";

        var profile = options.ProfileFor("some-other-key", sharedSecrets: false);

        Assert.Equal(["RecordQueues", "YouTubeUpload"], profile.Keys.Order());
        Assert.Equal("false", profile["YouTubeUpload"]);
    }

    // Ben's OAuth project: the operator put the complete credential set in
    // his key's override block, so it reaches that key whoever owns it.
    [Fact]
    public void A_keys_own_override_secrets_always_reach_it()
    {
        var options = Options();
        options.Profile["YouTubeUpload"] = "true";
        options.Profiles[BenKeyId]["YouTubeClientSecret"] = "ben-secret";
        options.Profiles[BenKeyId]["YouTubeRefreshToken"] = "ben-token";

        var profile = options.ProfileFor(BenKeyId, sharedSecrets: false);

        Assert.Equal("ben-id", profile["YouTubeClientId"]);
        Assert.Equal("ben-secret", profile["YouTubeClientSecret"]);
        Assert.Equal("ben-token", profile["YouTubeRefreshToken"]);
        Assert.Equal("true", profile["YouTubeUpload"]);
    }

    [Fact]
    public void The_operators_machine_gets_the_shared_secrets()
    {
        var options = Options();
        var profile = options.ProfileFor("ruben-key", sharedSecrets: true);

        Assert.Equal("shared-secret", profile["YouTubeClientSecret"]);
        Assert.Equal("shared-token", profile["YouTubeRefreshToken"]);
    }
}
