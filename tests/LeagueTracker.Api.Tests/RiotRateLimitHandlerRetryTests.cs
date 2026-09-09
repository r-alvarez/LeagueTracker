using System.Net;
using LeagueTracker.Api.Riot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

// Audit E4/E13: a 503 or a reset socket reached every caller, and the sync
// job took it for a corrupt match.
public class RiotRateLimitHandlerRetryTests
{
    private sealed class FakeKeys(string? key) : IRiotKeyProvider
    {
        public string? GetKey() => key;
    }

    // null = the connection dropped before any response.
    private sealed class ScriptedHandler(params HttpStatusCode?[] script) : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var outcome = script[Math.Min(Sends, script.Length - 1)];
            Sends++;
            if (outcome is not { } status) throw new HttpRequestException("connection reset");
            var response = new HttpResponseMessage(status) { Content = new StringContent("{}") };
            if (status is HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        }
    }

    private static (HttpClient Client, ScriptedHandler Inner) Client(string? key, params HttpStatusCode?[] script)
    {
        var options = Options.Create(new RiotOptions { TransientRetryBaseMs = 1 });
        var inner = new ScriptedHandler(script);
        var handler = new RiotRateLimitHandler(new RiotRateLimiter(options), new FakeKeys(key), options, NullLogger<RiotRateLimitHandler>.Instance)
        {
            InnerHandler = inner,
        };
        return (new HttpClient(handler), inner);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client) =>
        client.GetAsync("https://europe.api.riotgames.com/lol/match/v5/matches/EUW1_1/timeline");

    [Fact]
    public async Task A_5xx_is_retried_until_Riot_answers()
    {
        var (client, inner) = Client("key", HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, HttpStatusCode.OK);
        var resp = await Get(client);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, inner.Sends);
    }

    [Fact]
    public async Task A_dropped_connection_is_retried_like_a_5xx()
    {
        var (client, inner) = Client("key", null, HttpStatusCode.OK);
        var resp = await Get(client);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Sends);
    }

    [Fact]
    public async Task A_persistent_5xx_surfaces_after_the_retry_budget()
    {
        var (client, inner) = Client("key", HttpStatusCode.InternalServerError);
        var resp = await Get(client);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(1 + RiotRateLimitHandler.TransientRetries, inner.Sends);
    }

    [Fact]
    public async Task A_connection_that_never_comes_back_surfaces_after_the_retry_budget()
    {
        var (client, inner) = Client("key", [null]);
        await Assert.ThrowsAsync<HttpRequestException>(() => Get(client));
        Assert.Equal(1 + RiotRateLimitHandler.TransientRetries, inner.Sends);
    }

    [Fact]
    public async Task A_404_is_the_callers_to_handle_and_is_never_retried()
    {
        var (client, inner) = Client("key", HttpStatusCode.NotFound, HttpStatusCode.OK);
        var resp = await Get(client);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(1, inner.Sends);
    }

    [Fact]
    public async Task A_429_still_waits_and_asks_again()
    {
        var (client, inner) = Client("key", HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var resp = await Get(client);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Sends);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var (client, inner) = Client(null, HttpStatusCode.OK);
        await Assert.ThrowsAsync<RiotApiKeyMissingException>(() => Get(client));
        Assert.Equal(0, inner.Sends);
    }
}
