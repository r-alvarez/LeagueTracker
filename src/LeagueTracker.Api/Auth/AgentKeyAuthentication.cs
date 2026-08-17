using System.Security.Claims;
using System.Text.Encodings.Web;
using LeagueTracker.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Auth;

// X-Agent-Key -> a principal for the machine: its key record's id, name,
// owner and role as claims. Only an approved key authenticates; a pending or
// revoked one is told so on the challenge instead of a bare 401, because
// the agent's setup window shows that text to the person waiting.
public sealed class AgentKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AgentKeyStore keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "AgentKey";
    public const string HeaderName = "X-Agent-Key";
    public const string ItemKey = "agent-key-record";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[HeaderName].FirstOrDefault();
        if (key is not { Length: >= 16 }) return Task.FromResult(AuthenticateResult.NoResult());
        var record = keys.Find(key);
        if (record is null) return Task.FromResult(AuthenticateResult.Fail("agent key unknown"));
        Context.Items[ItemKey] = record;
        if (record.Status is not AgentKeyStatus.Approved) return Task.FromResult(AuthenticateResult.Fail($"agent is {record.Status.ToString().ToLowerInvariant()}"));

        keys.Touch(record, Context.Connection.RemoteIpAddress?.ToString());
        List<Claim> claims =
        [
            new(TrackerClaims.AgentId, record.Id),
            new(TrackerClaims.AgentName, record.Name),
            new(TrackerClaims.AgentRole, record.Role.ToString().ToLowerInvariant()),
        ];
        if (record.OwnerUserId is { Length: > 0 } owner) claims.Add(new(TrackerClaims.AgentOwner, owner));
        var identity = new ClaimsIdentity(claims, Scheme, TrackerClaims.AgentName, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var record = Context.Items[ItemKey] as AgentKeyRecord;
        Response.StatusCode = record is null ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        Response.ContentType = "application/json";
        return Response.WriteAsync(record is null
            ? "{\"error\":\"agent key missing or unknown - enrol first\"}"
            : $"{{\"error\":\"agent is {record.Status.ToString().ToLowerInvariant()}\"}}");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/json";
        return Response.WriteAsync("{\"error\":\"this agent may not act on that account\"}");
    }
}

public static class TrackerClaims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Admin = "admin";
    public const string AgentId = "agent_id";
    public const string AgentName = "agent_name";
    public const string AgentOwner = "agent_owner";
    public const string AgentRole = "agent_role";
}
