using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// The late checkpoint is the end state both the lane duel and stewardship
// grade against. Pinned because a fixed cap at 30 once read a 43-minute
// comeback at its single worst minute.
public class ReviewLateCheckpointTests
{
    private static List<TimelineAnalyzer.LaneDiffPoint> Checkpoints(params int[] minutes) => minutes
        .Select(min => new TimelineAnalyzer.LaneDiffPoint(min, Gold: min * 100, 0, 0, 0, 0, 0, 0, 0, [], []))
        .ToList();

    [Fact]
    public void Late_is_the_last_stored_checkpoint_not_a_fixed_thirty()
        => Assert.Equal(42, ReviewService.LateCheckpoint(Checkpoints(3, 10, 15, 20, 25, 30, 33, 36, 39, 40, 42))?.Min);

    [Fact]
    public void Late_follows_a_short_game_to_its_own_end()
        => Assert.Equal(27, ReviewService.LateCheckpoint(Checkpoints(10, 15, 20, 21, 24, 25, 27))?.Min);

    [Fact]
    public void Late_is_unset_when_the_game_never_reached_twenty()
        => Assert.Null(ReviewService.LateCheckpoint(Checkpoints(3, 6, 9, 10, 12, 15, 18)));

    [Fact]
    public void Late_order_does_not_depend_on_storage_order()
        => Assert.Equal(36, ReviewService.LateCheckpoint(Checkpoints(36, 20, 30, 25))?.Min);
}
