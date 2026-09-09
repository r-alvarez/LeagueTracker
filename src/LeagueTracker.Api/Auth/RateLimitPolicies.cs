using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace LeagueTracker.Api.Auth;

// Every claim start and verify is a summoner-v4 call on the one Riot key
// the whole deployment shares, and the icon space is 28 wide: a person
// proving one account needs a few of each per hour, a script needs none.
public static class RateLimitPolicies
{
    public const string ClaimStart = "claim-start";
    public const string ClaimVerify = "claim-verify";

    public static RateLimiterOptions AddClaimRateLimits(this RateLimiterOptions o)
    {
        o.AddPolicy(ClaimStart, http => PerCallerPerHour(http, 10));
        o.AddPolicy(ClaimVerify, http => PerCallerPerHour(http, 30));
        return o;
    }

    // The global ceiling: a visitor by address, a signed-in person by identity,
    // and none for an agent that authenticated (an upload is hundreds of chunk
    // PUTs). Only a principal the key handler issued counts - the header alone
    // was a bypass any anonymous caller could add (review of D7).
    public static RateLimitPartition<string> GlobalPartition(HttpContext http)
    {
        var identity = http.User.Identity;
        if (identity is { IsAuthenticated: true, AuthenticationType: AgentKeyAuthenticationHandler.SchemeName } && http.User.FindFirst(TrackerClaims.AgentId)?.Value is { Length: > 0 } agent)
            return RateLimitPartition.GetNoLimiter("agent:" + agent);
        if (identity is { IsAuthenticated: true } && http.User.FindFirst(TrackerClaims.UserId)?.Value is { Length: > 0 } user)
            return RateLimitPartition.GetSlidingWindowLimiter("user:" + user, _ => PerMinute(600));
        return RateLimitPartition.GetSlidingWindowLimiter("ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "anon"), _ => PerMinute(120));
    }

    private static SlidingWindowRateLimiterOptions PerMinute(int permits) =>
        new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 };

    private static RateLimitPartition<string> PerCallerPerHour(HttpContext http, int permits) =>
        RateLimitPartition.GetSlidingWindowLimiter(
            http.User.FindFirst(TrackerClaims.UserId)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new SlidingWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromHours(1), SegmentsPerWindow = 4, QueueLimit = 0 });
}
