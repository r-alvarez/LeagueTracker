using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// The poll cadence is per account: one friend's live game must not put every
// other tracked account on the 30 s clock (audit R-N1).
public class MatchPollerCadenceTests
{
    private static LiveGameSnapshot Snapshot() => new(1, "EUW1_1", 420, null, DateTime.UtcNow, 1, 100, []);

    [Fact]
    public void An_idle_account_keeps_the_configured_interval_with_a_30s_floor()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), MatchPollerService.Cadence(new LiveGameState(), 120));
        Assert.Equal(TimeSpan.FromSeconds(30), MatchPollerService.Cadence(new LiveGameState(), 5));
    }

    [Fact]
    public void Only_the_account_in_a_game_polls_every_30s()
    {
        var playing = new LiveGameState();
        playing.SetLive(Snapshot());
        Assert.Equal(TimeSpan.FromSeconds(30), MatchPollerService.Cadence(playing, 120));
        Assert.Equal(TimeSpan.FromSeconds(120), MatchPollerService.Cadence(new LiveGameState(), 120));
    }

    [Fact]
    public void A_game_that_just_ended_polls_every_15s_until_its_match_is_captured()
    {
        var live = new LiveGameState();
        live.SetLive(Snapshot());
        live.EndLiveIfAny(TimeSpan.FromMinutes(6));
        Assert.Equal(TimeSpan.FromSeconds(15), MatchPollerService.Cadence(live, 120));
        live.CaptureArrived();
        Assert.Equal(TimeSpan.FromSeconds(120), MatchPollerService.Cadence(live, 120));
    }

    [Fact]
    public void The_loop_sleeps_until_the_earliest_due_account()
    {
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.FromSeconds(7), MatchPollerService.SleepUntil([now.AddSeconds(90), now.AddSeconds(7)], now));
        Assert.Equal(TimeSpan.FromSeconds(1), MatchPollerService.SleepUntil([now.AddSeconds(-5)], now));
    }

    [Fact]
    public void The_loop_looks_up_at_least_every_30s_for_accounts_added_at_runtime()
    {
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.FromSeconds(30), MatchPollerService.SleepUntil([now.AddSeconds(120)], now));
        Assert.Equal(TimeSpan.FromSeconds(30), MatchPollerService.SleepUntil([], now));
    }
}
