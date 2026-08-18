using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

/// The percentile behind every "recent form" score. Like against like:
/// rolling windows of the earlier history, never the window against itself.
public class MatchMetricRowsTests
{
    private static List<Dictionary<string, double>> Rows(string key, params double[] values)
        => values.Select(v => new Dictionary<string, double> { [key] = v }).ToList();

    [Fact]
    public void Mean_ignores_rows_without_the_key_and_is_null_when_none_have_it()
    {
        var rows = Rows("cs", 10, 20);
        rows.Add(new Dictionary<string, double> { ["other"] = 99 });
        Assert.Equal(15, MatchMetricRows.Mean(rows, "cs"));
        Assert.Null(MatchMetricRows.Mean(rows, "kda"));
    }

    [Fact]
    public void Percentile_needs_a_recent_mean_and_at_least_five_baseline_games()
    {
        Assert.Null(MatchMetricRows.Percentile(Rows("cs", 1, 2, 3, 4, 5, 6), [], "cs", true));
        Assert.Null(MatchMetricRows.Percentile(Rows("cs", 1, 2, 3, 4), Rows("cs", 9), "cs", true));
    }

    [Fact]
    public void Percentile_ranks_the_recent_window_among_earlier_windows_of_the_same_length()
    {
        // Baseline 1..10, window of 2: rolling means 1.5, 2.5, ..., 9.5 (nine of them).
        var baseline = Rows("cs", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        Assert.Equal(100, MatchMetricRows.Percentile(baseline, Rows("cs", 20, 20), "cs", true));
        Assert.Equal(0, MatchMetricRows.Percentile(baseline, Rows("cs", 0, 0), "cs", true));
        // Mean 6 sits above 1.5..5.5 (five of nine) and below the rest -> 56.
        Assert.Equal(56, MatchMetricRows.Percentile(baseline, Rows("cs", 5, 7), "cs", true));
        // Mean 5.5 ties one rolling mean: four below + half a tie -> 50.
        Assert.Equal(50, MatchMetricRows.Percentile(baseline, Rows("cs", 5, 6), "cs", true));
    }

    [Fact]
    public void Percentile_flips_when_lower_is_better()
    {
        var baseline = Rows("deaths", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        Assert.Equal(0, MatchMetricRows.Percentile(baseline, Rows("deaths", 20, 20), "deaths", false));
        Assert.Equal(100, MatchMetricRows.Percentile(baseline, Rows("deaths", 0, 0), "deaths", false));
    }

    [Fact]
    public void Percentile_splits_ties()
    {
        // A baseline shorter than window + 4 ranks against single games: five
        // equal games and a recent window equal to them -> half below -> 50.
        var baseline = Rows("cs", 7, 7, 7, 7, 7);
        Assert.Equal(50, MatchMetricRows.Percentile(baseline, Rows("cs", 7, 7, 7), "cs", true));
    }
}
