using System.Security.Claims;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Auth;

// Who is asking, read once from the request's principal: a signed-in person
// (our user id, admin or not), an approved agent (its key record), or nobody.
public sealed class Caller(IHttpContextAccessor http, AgentKeyStore keys)
{
    private ClaimsPrincipal? Principal => http.HttpContext?.User;

    public bool IsUser => UserId is not null;
    public string? UserId => Principal?.Identity?.AuthenticationType is not AgentKeyAuthenticationHandler.SchemeName
        ? Principal?.FindFirstValue(TrackerClaims.UserId)
        : null;
    public string? Email => IsUser ? Principal?.FindFirstValue(TrackerClaims.Email) : null;
    public string? DisplayName => IsUser ? Principal?.FindFirstValue(TrackerClaims.Name) : null;
    public bool IsAdmin => IsUser && Principal?.HasClaim(TrackerClaims.Admin, "true") is true;

    public bool IsAgent => Agent is not null;
    public AgentKeyRecord? Agent =>
        Principal?.Identity?.AuthenticationType is AgentKeyAuthenticationHandler.SchemeName
        && Principal.FindFirstValue(TrackerClaims.AgentId) is { } id
            ? keys.ById(id)
            : null;
    public AgentRole? AgentRole => Agent?.Role;

    public bool IsAuthenticated => IsUser || IsAgent;

    // The owner test every policy reduces to: admin, or the account's owner,
    // or an agent whose owner is the account's owner (unbound agents only
    // while the rollout flag says so).
    public bool Owns(Accounts.Account account)
    {
        if (IsAdmin) return true;
        if (UserId is { } user) return account.OwnerUserId == user;
        if (Agent is { } agent) return agent.IsBound ? account.OwnerUserId == agent.OwnerUserId : keys.AllowUnbound;
        return false;
    }
}
