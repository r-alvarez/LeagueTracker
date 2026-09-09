using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Riot;

// Retries a 5xx or a dropped connection here, once for every caller, so the
// services only ever branch on 404.
public sealed class RiotRateLimitHandler(RiotRateLimiter limiter, IRiotKeyProvider keys, IOptions<RiotOptions> options, ILogger<RiotRateLimitHandler> logger)
    : DelegatingHandler
{
    public const int TransientRetries = 3;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = keys.GetKey() ?? throw new RiotApiKeyMissingException();
        var retried = 0;

        while (true)
        {
            await limiter.WaitBudgetAsync(ct);

            // An HttpRequestMessage can only be sent once; clone for the retry loop.
            using var attempt = new HttpRequestMessage(request.Method, request.RequestUri);
            attempt.Headers.Add("X-Riot-Token", key);

            HttpResponseMessage resp;
            try
            {
                resp = await base.SendAsync(attempt, ct);
            }
            catch (HttpRequestException ex) when (retried < TransientRetries)
            {
                retried++;
                var delay = Backoff(retried);
                logger.LogWarning("Riot request got no response ({Reason}); retry {Retry}/{Max} in {Seconds:0.0}s", ex.Message, retried, TransientRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                continue;
            }
            limiter.UpdateFromHeaders(resp);

            var status = (int)resp.StatusCode;
            if (status == 429)
            {
                var retryAfter = RetryAfter(resp) ?? TimeSpan.FromSeconds(5);
                limiter.TightenAfter429();
                logger.LogWarning("Rate limited (429) - waiting {Seconds}s and tightening margin", retryAfter.TotalSeconds);
                resp.Dispose();
                await Task.Delay(retryAfter, ct);
                continue;
            }
            if (status < 500 || retried >= TransientRetries) return resp;

            retried++;
            var wait = RetryAfter(resp) ?? Backoff(retried);
            logger.LogWarning("Riot answered {Status}; retry {Retry}/{Max} in {Seconds:0.0}s", status, retried, TransientRetries, wait.TotalSeconds);
            resp.Dispose();
            await Task.Delay(wait, ct);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage resp) =>
        resp.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero ? delta : null;

    // Up to 50% jitter: several accounts' pollers hitting one outage must not
    // come back in lockstep.
    private TimeSpan Backoff(int retry) =>
        TimeSpan.FromMilliseconds(options.Value.TransientRetryBaseMs * Math.Pow(2, retry - 1) * (1 + Random.Shared.NextDouble() * 0.5));
}

public sealed class RiotApiKeyMissingException()
    : Exception("No Riot API key configured. Set Riot:ApiKey, the RIOT_API_KEY environment variable, or point Riot:ApiKeyFile at a file whose first line is the key.");
