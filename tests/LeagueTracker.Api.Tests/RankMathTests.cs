using LeagueTracker.Api.Riot;

namespace LeagueTracker.Api.Tests;

/// The absolute LP scale everything averages and diffs on: a mistake here
/// skews every rank comparison on every page, so it gets pinned down.
public class RankMathTests
{
    [Theory]
    [InlineData("IRON", "IV", 0, 0)]
    [InlineData("IRON", "III", 50, 150)]
    [InlineData("iron", "iv", 20, 20)]
    [InlineData("SILVER", "IV", 0, 800)]
    [InlineData("GOLD", "I", 75, 1575)]
    [InlineData("EMERALD", "II", 10, 2210)]
    [InlineData("DIAMOND", "I", 99, 2799)]
    [InlineData("MASTER", "I", 0, 2800)]
    [InlineData("GRANDMASTER", "I", 450, 3250)]
    [InlineData("CHALLENGER", null, 1200, 4000)]
    public void ToValue_maps_tier_division_lp_onto_one_ladder(string tier, string? division, int lp, int expected)
        => Assert.Equal(expected, RankMath.ToValue(tier, division, lp));

    [Theory]
    [InlineData(null, "IV", 10)]
    [InlineData("", "IV", 10)]
    [InlineData("WOOD", "IV", 10)]
    [InlineData("GOLD", "V", 10)]
    [InlineData("GOLD", null, 10)]
    public void ToValue_is_null_for_unrankable_input(string? tier, string? division, int lp)
        => Assert.Null(RankMath.ToValue(tier, division, lp));

    [Theory]
    [InlineData(0, "Iron IV 0 LP")]
    [InlineData(150, "Iron III 50 LP")]
    [InlineData(1575, "Gold I 75 LP")]
    [InlineData(2799, "Diamond I 99 LP")]
    [InlineData(2800, "Master+ 0 LP")]
    [InlineData(3250, "Master+ 450 LP")]
    [InlineData(-40, "Iron IV 0 LP")]
    public void ToLabel_reverses_the_scale(double value, string expected)
        => Assert.Equal(expected, RankMath.ToLabel(value));

    [Theory]
    [InlineData("IRON", "IV", 0)]
    [InlineData("PLATINUM", "III", 42)]
    [InlineData("DIAMOND", "I", 99)]
    public void ToLabel_round_trips_every_tier_below_master(string tier, string division, int lp)
    {
        var value = RankMath.ToValue(tier, division, lp)!.Value;
        var expectedTier = tier[0] + tier[1..].ToLowerInvariant();
        Assert.Equal($"{expectedTier} {division} {lp} LP", RankMath.ToLabel(value));
    }

    [Fact]
    public void SelectEntryForQueue_prefers_the_games_queue_then_falls_back()
    {
        var solo = new LeagueEntryDto { QueueType = RankMath.SoloQueueType, Tier = "GOLD" };
        var flex = new LeagueEntryDto { QueueType = RankMath.FlexQueueType, Tier = "SILVER" };
        var tft = new LeagueEntryDto { QueueType = "RANKED_TFT", Tier = "IRON" };

        Assert.Same(solo, RankMath.SelectEntryForQueue([tft, flex, solo], RankMath.SoloQueueId));
        Assert.Same(flex, RankMath.SelectEntryForQueue([tft, flex, solo], RankMath.FlexQueueId));
        Assert.Same(flex, RankMath.SelectEntryForQueue([tft, flex], RankMath.SoloQueueId));
        Assert.Null(RankMath.SelectEntryForQueue([tft], RankMath.SoloQueueId));
    }

    [Fact]
    public void Every_named_queue_family_resolves_to_named_queues()
    {
        // The filter and the label table must stay in step: a family the URL
        // can ask for must only contain queues the UI can name.
        foreach (var family in new[] { "solo", "flex", "normal", "swiftplay", "aram", "arena", "urf" })
        {
            var ids = RankMath.QueueFamily(family);
            Assert.NotNull(ids);
            Assert.NotEmpty(ids);
            foreach (var id in ids) Assert.DoesNotContain("Queue ", RankMath.QueueName(id));
        }
        Assert.Null(RankMath.QueueFamily("bogus"));
    }

    [Fact]
    public void Queue_families_do_not_overlap()
    {
        // Each filter button means one thing: a Swiftplay game is not also a
        // Normal game, or the Normal count reads as draft games that never happened.
        var families = new[] { "solo", "flex", "normal", "swiftplay", "aram", "arena", "urf" };
        Dictionary<int, string> claimed = [];
        foreach (var family in families)
        {
            foreach (var id in RankMath.QueueFamily(family)!)
            {
                Assert.False(claimed.TryGetValue(id, out var other), $"queue {id} is in both {other} and {family}");
                claimed[id] = family;
            }
        }
    }
}
