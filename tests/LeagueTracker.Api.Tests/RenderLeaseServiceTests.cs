using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public class RenderLeaseServiceTests
{
    [Fact]
    public void A_claimed_job_stays_claimed_until_released()
    {
        var leases = new RenderLeaseService();
        Assert.True(leases.TryClaim("full:EUW1_1", "agent-a"));
        Assert.False(leases.TryClaim("full:EUW1_1", "agent-b"));
        Assert.True(leases.IsLeased("full:EUW1_1"));
        leases.Release("full:EUW1_1");
        Assert.True(leases.TryClaim("full:EUW1_1", "agent-b"));
    }

    [Fact]
    public void An_expired_lease_can_be_claimed_again()
    {
        var leases = new RenderLeaseService();
        Assert.True(leases.TryClaim("full:EUW1_1", "agent-a", TimeSpan.FromSeconds(-1)));
        Assert.False(leases.IsLeased("full:EUW1_1"));
        Assert.True(leases.TryClaim("full:EUW1_1", "agent-b"));
    }

    [Fact]
    public void A_full_game_lease_outlasts_the_render_and_the_upload()
    {
        // A 40-minute game rendered at speed 1: the fixed 30 minutes expired
        // mid-render and a second agent took the same job (audit M-N3).
        Assert.Equal(TimeSpan.FromMinutes(110), RenderLeaseService.FullGameLease(40 * 60));
        Assert.True(RenderLeaseService.FullGameLease(15 * 60) > TimeSpan.FromMinutes(30));
    }
}
