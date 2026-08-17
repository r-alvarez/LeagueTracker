using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Registry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Auth;

// The whole authorization vocabulary. Every endpoint names one; the handler
// below decides against the caller and the account the request is bound to.
public enum Access
{
    // Riot-derived data: anonymous once Auth:PublicReads is on, any signed-in
    // principal (person or agent) until then.
    Read,
    // Any signed-in person.
    User,
    // The account's owner, or an admin.
    Owner,
    Admin,
    // Any approved agent key (server-level agent routes).
    Agent,
    // Uploads for an account: a recorder whose owner owns it (or the owner).
    AgentRecorder,
    // Render work for an account: a renderer anywhere, or a recorder on its
    // owner's accounts. Agents only - leases and uploads are machine work.
    AgentRender,
    // Render state and the replay file: those agents, or the owner.
    RenderRead,
    // Recordings, clips, renders: public when the owner said so, else owner
    // or an agent that could have produced them.
    MediaRead,
}

public sealed class AccessRequirement(Access access) : IAuthorizationRequirement
{
    public Access Access { get; } = access;
}

public static class Policies
{
    public const string Read = nameof(Access.Read);
    public const string User = nameof(Access.User);
    public const string Owner = nameof(Access.Owner);
    public const string Admin = nameof(Access.Admin);
    public const string Agent = nameof(Access.Agent);
    public const string AgentRecorder = nameof(Access.AgentRecorder);
    public const string AgentRender = nameof(Access.AgentRender);
    public const string RenderRead = nameof(Access.RenderRead);
    public const string MediaRead = nameof(Access.MediaRead);

    public static void AddAll(AuthorizationOptions options)
    {
        foreach (var access in Enum.GetValues<Access>())
        {
            options.AddPolicy(access.ToString(), p => p.AddRequirements(new AccessRequirement(access)));
        }
        options.DefaultPolicy = options.GetPolicy(Read)!;
    }
}

public sealed class AccessHandler(IOptions<AuthOptions> auth) : AuthorizationHandler<AccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AccessRequirement requirement)
    {
        if (context.Resource is not HttpContext http) return Task.CompletedTask;
        var caller = http.RequestServices.GetRequiredService<Caller>();
        var accounts = http.RequestServices.GetRequiredService<AccountContext>();
        var account = accounts.IsBound ? accounts.Current : null;

        var allowed = requirement.Access switch
        {
            Access.Read => auth.Value.PublicReads || caller.IsAuthenticated,
            Access.User => caller.IsUser,
            Access.Admin => caller.IsAdmin,
            Access.Owner => account is not null && (caller.IsAdmin || (caller.IsUser && caller.Owns(account))),
            Access.Agent => caller.IsAgent,
            Access.AgentRecorder => account is not null && (caller.IsAgent ? caller.AgentRole is AgentRole.Recorder && caller.Owns(account) : caller.IsUser && caller.Owns(account)),
            Access.AgentRender => account is not null && caller.IsAgent && (caller.AgentRole is AgentRole.Renderer || caller.Owns(account)),
            Access.RenderRead => account is not null && (caller.IsAgent ? caller.AgentRole is AgentRole.Renderer || caller.Owns(account) : caller.IsUser && caller.Owns(account)),
            Access.MediaRead => account is not null && (account.MediaPublic
                ? auth.Value.PublicReads || caller.IsAuthenticated
                : caller.IsAgent ? caller.AgentRole is AgentRole.Renderer || caller.Owns(account) : caller.Owns(account)),
            _ => false,
        };
        if (allowed) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
