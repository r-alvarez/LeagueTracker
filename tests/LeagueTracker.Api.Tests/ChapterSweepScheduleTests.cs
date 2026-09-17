using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public sealed class ChapterSweepScheduleTests
{
    private static readonly TimeZoneInfo Uk = ChapterSweepSchedule.Zone("Europe/London");
    private static readonly TimeZoneInfo Pacific = ChapterSweepSchedule.Zone("America/Los_Angeles");

    private static DateTime Utc(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void The_nightly_pass_is_half_past_midnight_uk_time()
    {
        // 17 Sep 17:00 BST -> 18 Sep 00:30 BST = 17 Sep 23:30 UTC.
        Assert.Equal(Utc(9, 17, 23, 30), ChapterSweepSchedule.NextRunUtc(Utc(9, 17, 16, 0), Uk, Pacific, retryAfterReset: false));
        // Winter: GMT, so 00:30 UTC.
        Assert.Equal(Utc(12, 2, 0, 30), ChapterSweepSchedule.NextRunUtc(Utc(12, 1, 16, 0), Uk, Pacific, retryAfterReset: false));
    }

    [Fact]
    public void A_pass_the_quota_cut_short_tries_again_just_after_the_pacific_reset()
    {
        // Stopped at 18 Sep 00:30 BST; the quota resets at midnight PDT = 07:00 UTC.
        Assert.Equal(Utc(9, 18, 7, 15), ChapterSweepSchedule.NextRunUtc(Utc(9, 17, 23, 31), Uk, Pacific, retryAfterReset: true));
    }

    [Fact]
    public void Without_a_quota_stop_the_reset_is_not_a_run()
    {
        Assert.Equal(Utc(9, 18, 23, 30), ChapterSweepSchedule.NextRunUtc(Utc(9, 18, 7, 16), Uk, Pacific, retryAfterReset: false));
    }
}
