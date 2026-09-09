using System.Text.Json;
using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeagueTracker.Api.Tests;

// Audit E4: an analyzer tripping on one odd frame used to drop a healthy match.
public class MatchIngestResilienceTests : IDisposable
{
    private const string Me = "me-puuid";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    private MatchIngestService Ingest()
    {
        var context = new AccountContext(null!);
        context.Bind(new Account { GameName = "Me", TagLine = "EUW", Platform = "euw1", Region = "europe", DataDir = _root });
        var riot = new RiotApiClient(new HttpClient(), context);
        return new MatchIngestService(new RankLookupService(riot, new RankCache()), new DataPaths(context), NullLogger<MatchIngestService>.Instance);
    }

    private static string MatchJson(int participants = 10) => JsonSerializer.Serialize(new
    {
        metadata = new { matchId = "EUW1_1" },
        info = new
        {
            gameCreation = 1_700_000_000_000L,
            gameEndTimestamp = 1_700_001_800_000L,
            gameDuration = 1800,
            queueId = 420,
            gameMode = "CLASSIC",
            gameVersion = "14.1.1",
            participants = Enumerable.Range(1, participants).Select(pid => new
            {
                participantId = pid,
                puuid = pid == 1 ? Me : $"p{pid}",
                championName = $"C{pid}",
                teamId = pid <= 5 ? 100 : 200,
                teamPosition = "MIDDLE",
                win = pid <= 5,
            }).ToArray(),
        },
    });

    private static string TimelineJson(object timestamp) => JsonSerializer.Serialize(new
    {
        info = new
        {
            frames = new[]
            {
                new
                {
                    timestamp,
                    participantFrames = new Dictionary<string, object>
                    {
                        ["1"] = new { position = new { x = 1000, y = 1000 }, totalGold = 500, currentGold = 500, xp = 0, level = 1, minionsKilled = 0, jungleMinionsKilled = 0 },
                    },
                    events = (object[])[],
                },
            },
        },
    });

    [Fact]
    public async Task A_timeline_the_analyzer_cannot_read_still_ingests_the_match_without_it()
    {
        var match = await Ingest().BuildMatchAsync(MatchJson(), TimelineJson("not-a-number"), Me, withRanks: false, ranksAtGameTime: false, CancellationToken.None);

        Assert.Equal("EUW1_1", match.Id);
        Assert.False(match.HasTimeline);
        Assert.Equal(10, match.Participants.Count);
    }

    [Fact]
    public async Task A_readable_timeline_marks_the_match_as_having_one()
    {
        var match = await Ingest().BuildMatchAsync(MatchJson(), TimelineJson(60_000L), Me, withRanks: false, ranksAtGameTime: false, CancellationToken.None);

        Assert.True(match.HasTimeline);
    }

    [Fact]
    public async Task A_match_without_participants_is_unprocessable_for_good()
    {
        var ex = await Assert.ThrowsAsync<UnprocessableMatchException>(() =>
            Ingest().BuildMatchAsync(MatchJson(participants: 0), null, Me, withRanks: false, ranksAtGameTime: false, CancellationToken.None));

        Assert.True(MatchIngestService.IsUnprocessable(ex));
    }
}
