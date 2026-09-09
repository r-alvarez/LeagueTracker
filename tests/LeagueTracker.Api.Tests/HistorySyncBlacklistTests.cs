using System.Text.Json;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// Audit E4: what the sync job puts on the never-again list.
public class HistorySyncBlacklistTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    public void Riot_refusing_the_id_is_permanent(int status) =>
        Assert.True(HistorySyncService.IsPermanentFailure(new RiotApiException(status, "match-v5/matches/{id}", "")));

    // 408 and 429 are 4xx about the moment, not the match (review of E4).
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void A_timeout_a_rate_limit_or_a_5xx_is_retried_not_blacklisted(int status) =>
        Assert.False(HistorySyncService.IsPermanentFailure(new RiotApiException(status, "match-v5/matches/{id}", "")));

    [Fact]
    public void A_corrupt_payload_is_permanent()
    {
        Assert.True(HistorySyncService.IsPermanentFailure(new UnprocessableMatchException("no participants")));
        Assert.True(HistorySyncService.IsPermanentFailure(new JsonException("truncated")));
    }

    [Fact]
    public void Network_and_database_trouble_is_not_permanent()
    {
        Assert.False(HistorySyncService.IsPermanentFailure(new HttpRequestException("reset")));
        Assert.False(HistorySyncService.IsPermanentFailure(new IOException("disk")));
        Assert.False(HistorySyncService.IsPermanentFailure(new InvalidOperationException("GetInt32 on a non-number inside the analyzer")));
    }

    [Fact]
    public void A_dead_key_is_not_a_match_problem() =>
        Assert.False(HistorySyncService.IsPermanentFailure(new RiotApiException(403, "match-v5/matches/{id}", "")));
}
