using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using LeagueTracker.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace LeagueTracker.Api.Tests;

// Review of D7: the exemption keyed on the X-Agent-Key header alone, so any
// anonymous caller could add a junk value and leave the visitor ceiling.
public class GlobalRateLimitPartitionTests
{
    private static HttpContext Request(ClaimsPrincipal? user = null, string? agentHeader = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        if (agentHeader is not null) http.Request.Headers[AgentKeyAuthenticationHandler.HeaderName] = agentHeader;
        if (user is not null) http.User = user;
        return http;
    }

    private static ClaimsPrincipal ApprovedAgent() =>
        new(new ClaimsIdentity([new Claim(TrackerClaims.AgentId, "k1")], AgentKeyAuthenticationHandler.SchemeName));

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-real-key")]
    [InlineData("pending-or-revoked-keys-never-become-a-principal")]
    public void A_header_without_an_authenticated_agent_stays_on_the_visitor_limit(string? header)
    {
        var partition = RateLimitPolicies.GlobalPartition(Request(agentHeader: header));
        Assert.Equal("ip:203.0.113.9", partition.PartitionKey);
        Assert.IsType<SlidingWindowRateLimiter>(partition.Factory(partition.PartitionKey));
    }

    [Fact]
    public void An_authenticated_agent_is_exempt()
    {
        var partition = RateLimitPolicies.GlobalPartition(Request(ApprovedAgent(), "whatever-the-handler-accepted"));
        Assert.Equal("agent:k1", partition.PartitionKey);
        Assert.Equal(RateLimitPartition.GetNoLimiter("x").Factory("x").GetType(), partition.Factory(partition.PartitionKey).GetType());
    }

    [Fact]
    public void A_signed_in_person_is_limited_by_identity_even_with_a_stray_agent_header()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(TrackerClaims.UserId, "u1")], "Cookies"));
        var partition = RateLimitPolicies.GlobalPartition(Request(user, "junk"));
        Assert.Equal("user:u1", partition.PartitionKey);
    }
}
