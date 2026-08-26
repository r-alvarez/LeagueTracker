using LeagueTracker.Api.Data;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// The champion table's aggregates, on the two definitions the audit found
// wrong: KDA as a mean of ratios (A-N1) and multikills summed from Riot's
// inclusive counters (A-N2).
public class ReportsAggregateTests
{
    private static Match Game(int kills, int deaths, int assists, int triples = 0, int quadras = 0, int pentas = 0) => new()
    {
        Kills = kills, Deaths = deaths, Assists = assists,
        TripleKills = triples, QuadraKills = quadras, PentaKills = pentas,
    };

    [Fact]
    public void Kda_is_the_ratio_of_sums_not_the_mean_of_ratios()
    {
        // 15/15/11 overall -> 1.73. The mean of per-game ratios would be
        // (15 + 0.4 + 1.4) / 3 = 5.6, carried by the one zero-death game.
        List<Match> games = [Game(10, 0, 5), Game(2, 10, 2), Game(3, 5, 4)];
        Assert.Equal(1.73, Reports.AggregateKda(games));
    }

    [Fact]
    public void Kda_matches_the_printed_kda_line()
    {
        // Ahri on the audit corpus read 7.5 / 4.7 / 7.4 next to "(4.7)"; the
        // line itself says (7.5 + 7.4) / 4.7 = 3.17.
        List<Match> games = [Game(8, 5, 7), Game(7, 4, 8), Game(7, 5, 7), Game(8, 5, 8)];
        Assert.Equal(3.16, Reports.AggregateKda(games));
    }

    [Fact]
    public void Kda_with_no_deaths_at_all_counts_one_death()
    {
        Assert.Equal(12, Reports.AggregateKda([Game(8, 0, 4)]));
        Assert.Equal(0, Reports.AggregateKda([]));
    }

    [Fact]
    public void Multikills_are_exclusive_per_tier()
    {
        // Riot ticks every threshold crossed: one penta arrives as 1/1/1,
        // one quadra as 1/1/0, one plain triple as 1/0/0.
        List<Match> games = [Game(0, 0, 0, triples: 1, quadras: 1, pentas: 1), Game(0, 0, 0, triples: 1, quadras: 1), Game(0, 0, 0, triples: 1)];
        Assert.Equal(1, Reports.ExclusiveTriples(games));
        Assert.Equal(1, Reports.ExclusiveQuadras(games));
        Assert.Equal(1, games.Sum(g => g.PentaKills));
    }

    [Fact]
    public void Multikills_metric_counts_a_penta_once()
    {
        var row = MatchMetricRows.ComputeRow(Game(0, 0, 0, triples: 1, quadras: 1, pentas: 1), 0, 0);
        Assert.Equal(1, row["multiKills"]);
    }
}
