using System.Security.Claims;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Auth;

// Who is asking, read once from the request's principal: a signed-in person
// (our user id, admin or not), an approved agent (its key record), or nobody.
public sealed class Caller(IHttpContextAccessor http, AgentKeyStore keys, UserStore users)
{
    private ClaimsPrincipal? Principal => http.HttpContext?.User;
    private bool? _admin;

    public bool IsUser => UserId is not null;
    public string? UserId => Principal?.Identity?.AuthenticationType is not AgentKeyAuthenticationHandler.SchemeName
        ? Principal?.FindFirstValue(TrackerClaims.UserId)
        : null;
    public string? Email => IsUser ? Principal?.FindFirstValue(TrackerClaims.Email) : null;
    public string? DisplayName => IsUser ? Principal?.FindFirstValue(TrackerClaims.Name) : null;
    // Read from the registry, not the cookie: promoting (or demoting) someone
    // takes effect on their next request, not their next sign-in. One lookup
    // per request, on a tiny table.
    public bool IsAdmin => IsUser && (_admin ??= users.ById(UserId)?.IsAdmin ?? false);

    public bool IsAgent => Agent is not null;
    public AgentKeyRecord? Agent =>
        Principal?.Identity?.AuthenticationType is AgentKeyAuthenticationHandler.SchemeName
        && Principal.FindFirstValue(TrackerClaims.AgentId) is { } id
            ? keys.ById(id)
            : null;
    public AgentRole? AgentRole => Agent?.Role;

    public bool IsAuthenticated => IsUser || IsAgent;

    public IEnumerable<Accounts.Account> DiscoverAgentAccounts(IEnumerable<Accounts.Account> accounts) =>
        Agent?.Role is Registry.AgentRole.Renderer ? accounts : accounts.Where(Owns);

    // The desktop review window is always personal, including on a machine
    // that also renders for everyone. A renderer's broad queue scope must not
    // turn into a list of other people's gameplay on that PC.
    public IEnumerable<Accounts.Account> DiscoverReviewAccounts(IEnumerable<Accounts.Account> accounts)
    {
        var agent = Agent;
        if (agent is not { IsBound: true }) return [];
        return accounts.Where(account => account.OwnerUserId == agent.OwnerUserId || agent.MayActFor(account.Id));
    }

    // The owner test every policy reduces to: admin, or the account's owner,
    // or an agent whose owner is the account's owner - or that carries an
    // explicit grant for this account (the shared-PC case: a friend's games
    // are recorded on this machine, so only this machine can deliver them).
    // Unbound agents pass only while the rollout flag says so.
    public bool Owns(Accounts.Account account)
    {
        if (IsAdmin) return true;
        if (UserId is { } user) return account.OwnerUserId == user;
        if (Agent is { } agent) return agent.IsBound
            ? account.OwnerUserId == agent.OwnerUserId || agent.MayActFor(account.Id)
            : keys.AllowUnbound;
        return false;
    }
}
