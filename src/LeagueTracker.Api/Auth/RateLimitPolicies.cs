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

    private static RateLimitPartition<string> PerCallerPerHour(HttpContext http, int permits) =>
        RateLimitPartition.GetSlidingWindowLimiter(
            http.User.FindFirst(TrackerClaims.UserId)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new SlidingWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromHours(1), SegmentsPerWindow = 4, QueueLimit = 0 });
}
