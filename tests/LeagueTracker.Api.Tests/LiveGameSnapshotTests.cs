using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

/// The live banner's clock reads off this snapshot; a wrong anchor is a clock
/// that disagrees with the stream for the whole game.
public class LiveGameSnapshotTests
{
    private const string Me = "me-puuid";

    private static string Spectator(long gameStartTime) => $$"""
        {
          "gameId": 7954191662, "platformId": "EUW1", "gameQueueConfigId": 400,
          "gameStartTime": {{gameStartTime}}, "gameLength": -96,
          "participants": [
            { "championId": 901, "teamId": 200, "riotId": "TheCosmicPeach#TTV", "puuid": "{{Me}}" },
            { "championId": 10, "teamId": 100, "riotId": "Yunara#Ionia", "puuid": "other" }
          ]
        }
        """;

    [Fact]
    public void Clock_zero_is_thirty_seconds_after_spectators_start()
    {
        // Measured against match-v5 gameStartTimestamp on three EUW games:
        // spectator's gameStartTime leads the in-game clock by 30.1s every time.
        var snapshot = LiveGameSnapshot.Parse(Spectator(1787047782070), Me);   // 2026-08-18T10:09:42.070Z

        Assert.Equal(new DateTime(2026, 8, 18, 10, 9, 42, 70, DateTimeKind.Utc), snapshot.StartedUtc);
        Assert.Equal(new DateTime(2026, 8, 18, 10, 10, 12, 70, DateTimeKind.Utc), snapshot.ClockStartUtc);
        Assert.Equal("EUW1_7954191662", snapshot.MatchId);
        Assert.Equal(901, snapshot.MyChampionId);
        Assert.Equal(200, snapshot.MyTeamId);
    }

    [Fact]
    public void No_start_time_yet_means_no_clock()
    {
        var snapshot = LiveGameSnapshot.Parse(Spectator(0), Me);

        Assert.Null(snapshot.StartedUtc);
        Assert.Null(snapshot.ClockStartUtc);
    }
}
