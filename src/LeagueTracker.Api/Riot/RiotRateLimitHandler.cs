namespace LeagueTracker.Api.Riot;

/// Injects the API key, spends rate budget before each request, learns real
/// limits from response headers, and waits out 429s (Retry-After).
public sealed class RiotRateLimitHandler(RiotRateLimiter limiter, IRiotKeyProvider keys, ILogger<RiotRateLimitHandler> logger)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = keys.GetKey() ?? throw new RiotApiKeyMissingException();

        while (true)
        {
            await limiter.WaitBudgetAsync(ct);

            // An HttpRequestMessage can only be sent once; clone for the retry loop.
            using var attempt = new HttpRequestMessage(request.Method, request.RequestUri);
            attempt.Headers.Add("X-Riot-Token", key);

            var resp = await base.SendAsync(attempt, ct);
            limiter.UpdateFromHeaders(resp);

            if ((int)resp.StatusCode != 429) return resp;

            var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
            if (retryAfter <= TimeSpan.Zero) retryAfter = TimeSpan.FromSeconds(5);
            limiter.TightenAfter429();
            logger.LogWarning("Rate limited (429) - waiting {Seconds}s and tightening margin", retryAfter.TotalSeconds);
            resp.Dispose();
            await Task.Delay(retryAfter, ct);
        }
    }
}

public sealed class RiotApiKeyMissingException()
    : Exception("No Riot API key configured. Set Riot:ApiKey, the RIOT_API_KEY environment variable, or point Riot:ApiKeyFile at a file whose first line is the key.");
