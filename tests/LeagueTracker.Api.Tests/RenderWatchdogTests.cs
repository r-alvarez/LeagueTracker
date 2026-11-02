using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public sealed class RenderWatchdogTests
{
    private static readonly DateTime Noon = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private static AgentLive Renderer(bool online, bool paused = false, string state = "idle", string? detail = null, string? error = null, DateTime? seen = null) =>
        new("k1", "rjav-agent01", "2026.9.19.1000", "renderer", paused, state, detail, null, true, error, "RJAV-AGENT01", null, null, false, seen ?? Noon, online);

    [Fact]
    public void Fresh_work_gets_the_full_threshold_before_it_counts_as_stalled()
    {
        var since = new Dictionary<string, DateTime>();
        var pending = new Dictionary<string, int> { ["a"] = 3 };

        Assert.Empty(RenderWatchdog.FindStalls(pending, since, _ => null, Noon, TwoHours));
        Assert.Empty(RenderWatchdog.FindStalls(pending, since, _ => null, Noon.AddMinutes(119), TwoHours));
        var stall = Assert.Single(RenderWatchdog.FindStalls(pending, since, _ => null, Noon.AddHours(2), TwoHours));
        Assert.Equal(("a", 3, Noon), (stall.AccountId, stall.Pending, stall.SinceUtc));
    }

    [Fact]
    public void Progress_restarts_the_clock()
    {
        var since = new Dictionary<string, DateTime> { ["a"] = Noon };
        var pending = new Dictionary<string, int> { ["a"] = 3 };

        Assert.Empty(RenderWatchdog.FindStalls(pending, since, _ => Noon.AddHours(1), Noon.AddHours(2.5), TwoHours));
        var stall = Assert.Single(RenderWatchdog.FindStalls(pending, since, _ => Noon.AddHours(1), Noon.AddHours(3), TwoHours));
        Assert.Equal(Noon.AddHours(1), stall.SinceUtc);
    }

    [Fact]
    public void Progress_from_before_the_backlog_does_not_shorten_the_wait()
    {
        // The renderer finished yesterday's work at 10:00; new work at noon
        // still gets its full two hours.
        var since = new Dictionary<string, DateTime>();
        var pending = new Dictionary<string, int> { ["a"] = 1 };

        RenderWatchdog.FindStalls(pending, since, _ => Noon.AddHours(-2), Noon, TwoHours);
        Assert.Empty(RenderWatchdog.FindStalls(pending, since, _ => Noon.AddHours(-2), Noon.AddMinutes(90), TwoHours));
    }

    [Fact]
    public void An_emptied_queue_forgets_its_backlog()
    {
        var since = new Dictionary<string, DateTime> { ["a"] = Noon.AddHours(-5) };

        RenderWatchdog.FindStalls(new Dictionary<string, int>(), since, _ => null, Noon, TwoHours);
        Assert.Empty(since);
        Assert.Empty(RenderWatchdog.FindStalls(new Dictionary<string, int> { ["a"] = 1 }, since, _ => null, Noon.AddMinutes(1), TwoHours));
    }

    [Fact]
    public void Each_account_is_judged_on_its_own_progress()
    {
        var since = new Dictionary<string, DateTime> { ["a"] = Noon.AddHours(-3), ["b"] = Noon.AddHours(-3) };
        var pending = new Dictionary<string, int> { ["a"] = 2, ["b"] = 5 };

        var stall = Assert.Single(RenderWatchdog.FindStalls(pending, since, id => id is "a" ? Noon.AddMinutes(-10) : null, Noon, TwoHours));
        Assert.Equal("b", stall.AccountId);
    }

    [Fact]
    public void The_cause_names_a_renderer_that_stopped_reporting()
    {
        var cause = RenderWatchdog.Cause([Renderer(online: false, seen: Noon.AddHours(-7).AddMinutes(-5))], Noon);
        Assert.StartsWith("rjav-agent01 last reported 7h 5m ago", cause);
    }

    [Fact]
    public void The_cause_quotes_what_an_online_renderer_is_stuck_on()
    {
        var cause = RenderWatchdog.Cause([Renderer(online: true, state: "waiting", detail: "User is active", error: "patch mismatch")], Noon);
        Assert.Equal("rjav-agent01 is online but not finishing jobs - it reports \"waiting: User is active\", last error: patch mismatch.", cause);
    }

    [Fact]
    public void The_cause_says_paused_and_says_when_no_renderer_ever_reported()
    {
        Assert.Equal("rjav-agent01 is paused from its tray icon.", RenderWatchdog.Cause([Renderer(online: true, paused: true)], Noon));
        Assert.StartsWith("No render machine has reported", RenderWatchdog.Cause([], Noon));
    }
}
