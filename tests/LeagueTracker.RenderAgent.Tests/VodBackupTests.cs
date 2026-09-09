using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeagueTracker.RenderAgent.Tests;

public sealed class VodBackupTests
{
    private const string Metadata = """{"matchId":"EUW1_123","videoFile":"game.mp4","recordingStartUtc":"2026-09-01T12:00:00Z","recordingEndUtc":"2026-09-01T12:30:00Z"}""";
    private static JsonObject Status() => new() { ["exists"] = true, ["sizeBytes"] = 100, ["meta"] = JsonNode.Parse(Metadata) };

    [Fact]
    public async Task Exact_server_copy_is_confirmed_without_downloading_video()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://tracker.test/api/matches/EUW1_123/vod/status?includeApm=false", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent(Status().ToJsonString(), Encoding.UTF8, "application/json") };
        }));
        using var local = JsonDocument.Parse(Metadata);
        Assert.True(await VodBackup.ConfirmAsync(http, "https://tracker.test/api", "EUW1_123", 100, local.RootElement, CancellationToken.None));
    }

    [Theory]
    [InlineData("sidecars")]
    [InlineData("old-server")]
    [InlineData("partial")]
    [InlineData("other-recording")]
    [InlineData("other-match")]
    [InlineData("other-start")]
    [InlineData("other-end")]
    [InlineData("missing-meta")]
    public void Unproven_backups_are_not_safe_to_rotate(string scenario)
    {
        var status = Status();
        switch (scenario)
        {
            case "sidecars": status["exists"] = false; break;
            case "old-server": status.Remove("sizeBytes"); status["sizeMb"] = 0; break;
            case "partial": status["sizeBytes"] = 99; break;
            case "other-recording": status["meta"]!["videoFile"] = "other.mp4"; break;
            case "other-match": status["meta"]!["matchId"] = "EUW1_456"; break;
            case "other-start": status["meta"]!["recordingStartUtc"] = "2026-09-01T12:01:00Z"; break;
            case "other-end": status["meta"]!["recordingEndUtc"] = "2026-09-01T12:31:00Z"; break;
            case "missing-meta": status["meta"] = null; break;
        }
        using var local = JsonDocument.Parse(Metadata);
        using var remote = JsonDocument.Parse(status.ToJsonString());
        Assert.False(VodBackup.Matches(remote.RootElement, 100, local.RootElement));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>login</html>")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task Unavailable_or_invalid_status_keeps_the_local_copy(HttpStatusCode code, string body)
    {
        using var http = new HttpClient(new Handler(_ => new(code) { Content = new StringContent(body) }));
        using var local = JsonDocument.Parse(Metadata);
        Assert.False(await VodBackup.ConfirmAsync(http, "https://tracker.test/api", "EUW1_123", 100, local.RootElement, CancellationToken.None));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
