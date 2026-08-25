using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Auth;

// /api/me: what a signed-in person owns - their accounts and their machines
// (join codes, approve, revoke, restart). /api/admin: the same over everyone,
// plus the hand that assigns owners. Distinct roots from /api/agent so no
// edge rule can ever confuse machine and human routes again (audit T-B7).
public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/api/me").RequireAuthorization(Policies.User);
        var admin = app.MapGroup("/api/admin").RequireAuthorization(Policies.Admin);

        me.MapGet("/", (Caller caller, AccountRegistry accounts, UserStore users) =>
        {
            var user = users.ById(caller.UserId)!;
            return Results.Ok(new
            {
                user.Id, user.Email, user.DisplayName, user.IsAdmin,
                Logins = user.Logins.Select(l => new { l.Issuer, l.LastUsedUtc }),
                Accounts = accounts.OwnedBy(user.Id).Select(a => new { a.Id, a.RiotId, a.Slug, Region = a.RegionCode, Path = a.UrlPath, a.MediaPublic, a.HideLp }),
            });
        });

        // --- My machines ----------------------------------------------------------

        // Every key this person may see - theirs, plus the renderers that serve
        // everyone; all of them for an admin - each joined with the heartbeat
        // of the agent running under it and the logs it shipped, plus the
        // newest published build, so one table says who is on what.
        me.MapGet("/agents", (Caller caller, AgentKeyStore keys, AgentRegistry agents, UserStore users, AccountRegistry accounts) =>
        {
            var emails = caller.IsAdmin ? users.All().ToDictionary(u => u.Id, u => u.Email) : [];
            var visible = caller.IsAdmin ? keys.All : keys.All.Where(k => k.OwnerUserId == caller.UserId || k.Role is AgentRole.Renderer).ToList();
            return Results.Ok(new
            {
                LatestVersion = agents.Latest()?.Version,
                Keys = visible.Select(k => KeyView(k, k.OwnerUserId is { } o && emails.TryGetValue(o, out var e) ? e : null,
                    agents.Find(k.Id, caller.UserId, caller.IsAdmin), agents.Logs(k.Id), k.OwnerUserId == caller.UserId, accounts)),
                JoinCodes = keys.OpenJoinCodes(caller.UserId!).Select(c => new { c.Code, Role = c.Role.ToString().ToLowerInvariant(), c.ExpiresUtc }),
            });
        });

        // A code the owner reads out to the machine's setup window: 15 minutes,
        // one use, and the key it admits is theirs from the first byte.
        me.MapPost("/agents/join-code", (Caller caller, AgentKeyStore keys, string role = "recorder") =>
        {
            if (!Enum.TryParse<AgentRole>(role, ignoreCase: true, out var parsed)) return Results.BadRequest(new { error = "role must be recorder or renderer" });
            // Renderers reach every account: only an admin may admit one.
            if (parsed is AgentRole.Renderer && !caller.IsAdmin) return Results.Forbid();
            var code = keys.MintJoinCode(caller.UserId!, parsed);
            return Results.Ok(new { code.Code, Pretty = $"{code.Code[..4]}-{code.Code[4..]}", Role = role.ToLowerInvariant(), code.ExpiresUtc });
        });

        me.MapPost("/agents/{id}/approve", (string id, Caller caller, AgentKeyStore keys) =>
            Own(caller, keys, id) is { } key ? (keys.Decide(key.Id, AgentKeyStatus.Approved) ? Results.Ok() : Results.NotFound()) : Results.NotFound());
        me.MapPost("/agents/{id}/revoke", (string id, Caller caller, AgentKeyStore keys) =>
            Own(caller, keys, id) is { } key ? (keys.Decide(key.Id, AgentKeyStatus.Revoked) ? Results.Ok() : Results.NotFound()) : Results.NotFound());
        me.MapDelete("/agents/{id}", (string id, Caller caller, AgentKeyStore keys) =>
            Own(caller, keys, id) is { } key ? (keys.Delete(key.Id) ? Results.NoContent() : Results.NotFound()) : Results.NotFound());
        // Queue a restart for an agent - it obeys on its next heartbeat when idle
        // (re-reads the profile and self-updates on the way back up).
        me.MapPost("/agents/{id}/restart", (string id, Caller caller, AgentKeyStore keys, AgentRegistry agents) =>
            Own(caller, keys, id) is { } key ? (agents.Queue(key.Id, "restart") ? Results.Ok() : Results.NotFound()) : Results.NotFound());
        me.MapPost("/agents/{id}/dismiss-error", (string id, Caller caller, AgentKeyStore keys, AgentRegistry agents) =>
            Own(caller, keys, id) is { } key ? (agents.DismissError(key.Id) ? Results.Ok() : Results.NotFound()) : Results.NotFound());
        // Ask an agent for its log: it ships the tail of agent.log on its next
        // heartbeat (no idle gate - reading a file disturbs nothing).
        me.MapPost("/agents/{id}/sendlog", (string id, Caller caller, AgentKeyStore keys, AgentRegistry agents) =>
            Own(caller, keys, id) is { } key ? (agents.Queue(key.Id, "sendlog") ? Results.Ok() : Results.NotFound()) : Results.NotFound());
        me.MapGet("/agents/{id}/logs", (string id, Caller caller, AgentKeyStore keys, AgentRegistry agents) =>
            Own(caller, keys, id) is { } key ? Results.Ok(agents.Logs(key.Id)) : Results.NotFound());
        me.MapGet("/agents/{id}/logs/{file}", (string id, string file, Caller caller, AgentKeyStore keys, AgentRegistry agents) =>
            Own(caller, keys, id) is { } key && agents.LogPath(key.Id, file) is { } path
                ? Results.File(path, "text/plain; charset=utf-8")
                : Results.NotFound());

        // --- Claiming a Riot account --------------------------------------------------

        me.MapGet("/claims", (Caller caller, ClaimService claims) => Results.Ok(claims.Mine(caller.UserId!)));

        me.MapPost("/claims", async (ClaimRequest request, Caller caller, ClaimService claims, CancellationToken ct) =>
        {
            var (claim, error) = await claims.StartAsync(caller.UserId!, request.AccountId, ct);
            return claim is not null ? Results.Ok(claim) : Results.BadRequest(new { error });
        });

        me.MapPost("/claims/{id}/verify", async (string id, Caller caller, ClaimService claims, CancellationToken ct) =>
        {
            var (claim, verified, error) = await claims.VerifyAsync(caller.UserId!, id, ct);
            return claim is null ? Results.NotFound(new { error }) : Results.Ok(new { claim, verified, error });
        });

        // --- Admin ----------------------------------------------------------------

        admin.MapGet("/users", (UserStore users, AccountRegistry accounts, AgentKeyStore keys, Auth0ManagementClient auth0) => Results.Ok(new
        {
            // Whether "Add a person" can reach the provider on this instance;
            // without it the row is still made and the page says the rest.
            InvitesConfigured = auth0.Configured,
            Users = users.All().Select(u => UserView(u, accounts, keys)),
        }));

        // Add a person: our row first (assignable at once), then the identity
        // at Auth0 and Auth0's own "set your password" mail. A provider
        // failure leaves the row - Resend finishes the job later.
        admin.MapPost("/users", async (InviteRequest request, Caller caller, UserStore users, AccountRegistry accounts, AgentKeyStore keys, Auth0ManagementClient auth0, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (request.Email is not { Length: > 3 } email || !email.Contains('@')) return Results.BadRequest(new { error = "Type the person's email address" });
            if (users.Invite(email, request.DisplayName, caller.UserId!) is not { } user) return Results.Conflict(new { error = $"{email.Trim().ToLowerInvariant()} is already here" });
            var (mailed, warning) = await SendInviteAsync(user, users, auth0, logs.CreateLogger("LeagueTracker.Invites"), ct);
            return Results.Ok(new { user = UserView(users.ById(user.Id)!, accounts, keys), mailed, warning });
        }).RequireRateLimiting("invite");

        admin.MapPost("/users/{id}/invite", async (string id, UserStore users, AccountRegistry accounts, AgentKeyStore keys, Auth0ManagementClient auth0, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (users.ById(id) is not { IsInvitedPending: true } user) return Results.NotFound(new { error = "only a person who has not signed in yet can be re-invited" });
            var (mailed, warning) = await SendInviteAsync(user, users, auth0, logs.CreateLogger("LeagueTracker.Invites"), ct);
            return Results.Ok(new { user = UserView(users.ById(user.Id)!, accounts, keys), mailed, warning });
        }).RequireRateLimiting("invite");

        // The link the mail carries, for the admin to hand over when the mail
        // does not arrive. Shown once; nothing of it is stored.
        admin.MapPost("/users/{id}/invite-link", async (string id, HttpContext http, UserStore users, Auth0ManagementClient auth0, CancellationToken ct) =>
        {
            if (users.ById(id) is not { IsInvitedPending: true } user) return Results.NotFound(new { error = "only a person who has not signed in yet gets an invite link" });
            if (!auth0.Configured) return Results.Problem("Auth0 management is not configured on this instance (Auth:Management)", statusCode: 503);
            try
            {
                var providerId = user.ProviderUserId ?? await auth0.EnsureUserAsync(user.Email, user.DisplayName, ct);
                if (user.ProviderUserId is null) users.SetProviderUserId(user.Id, providerId);
                var (url, expires) = await auth0.PasswordSetupLinkAsync(providerId, $"{http.Request.Scheme}://{http.Request.Host}/", TimeSpan.FromDays(7), ct);
                return Results.Ok(new { url, expiresUtc = expires });
            }
            catch (Auth0Exception ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
        }).RequireRateLimiting("invite");

        admin.MapDelete("/users/{id}", async (string id, Caller caller, UserStore users, Auth0ManagementClient auth0, CancellationToken ct) =>
        {
            if (id == caller.UserId) return Results.BadRequest(new { error = "you cannot remove yourself" });
            if (users.ById(id) is not { IsInvitedPending: true } user) return Results.NotFound(new { error = "only a person who has not signed in yet can be removed" });
            if (user.ProviderUserId is { } providerId && auth0.Configured)
            {
                try { await auth0.DeleteUserAsync(providerId, ct); }
                catch (Auth0Exception ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
            }
            return users.DeleteInvited(id) ? Results.NoContent() : Results.NotFound();
        });

        admin.MapGet("/agents", (AgentKeyStore keys, UserStore users, AgentRegistry agents, AccountRegistry accounts) =>
        {
            var byId = users.All().ToDictionary(u => u.Id, u => u.Email);
            return Results.Ok(new
            {
                LatestVersion = agents.Latest()?.Version,
                Keys = keys.All.Select(k => KeyView(k, k.OwnerUserId is { } o && byId.TryGetValue(o, out var e) ? e : null, agents.Find(k.Id, null, admin: true), agents.Logs(k.Id), false, accounts)),
            });
        });

        // The owner and role of a machine, by hand: the four keys from before
        // ownership existed, or a friend who enrolled without a code. ActsFor
        // (account ids) grants the machine other people's accounts on top of
        // its owner's - for the accounts that get played on this very PC;
        // null leaves the grant as it is, [] clears it.
        admin.MapPost("/agents/{id}/assign", (string id, AssignRequest request, AgentKeyStore keys, UserStore users, AccountRegistry accounts) =>
        {
            string? ownerId = null;
            if (request.OwnerEmail is { Length: > 0 } email)
            {
                if (users.ByEmail(email) is not { } owner) return Results.NotFound(new { error = $"no user with email {email} - they must sign in once first, or be named in configuration" });
                ownerId = owner.Id;
            }
            AgentRole? role = request.Role is { Length: > 0 } r && Enum.TryParse<AgentRole>(r, ignoreCase: true, out var parsed) ? parsed : null;
            if (request.ActsFor is { } grant && grant.FirstOrDefault(a => accounts.ById(a) is null) is { } unknown)
            {
                return Results.NotFound(new { error = $"no account with id {unknown}" });
            }
            return keys.Assign(id, ownerId, role, request.ActsFor) ? Results.Ok() : Results.NotFound();
        });

        admin.MapPost("/accounts/{id}/owner", (string id, AssignRequest request, AccountRegistry accounts, UserStore users) =>
        {
            if (accounts.ById(id) is not { } account) return Results.NotFound();
            string? ownerId = null;
            if (request.OwnerEmail is { Length: > 0 } email)
            {
                if (users.ByEmail(email) is not { } owner) return Results.NotFound(new { error = $"no user with email {email}" });
                ownerId = owner.Id;
            }
            accounts.Update(account, a => a.OwnerUserId = ownerId);
            return Results.Ok(new { account.Id, account.RiotId, account.OwnerUserId });
        });

        admin.MapPost("/users/{id}/admin", (string id, bool admin, UserStore users) =>
            users.SetAdmin(id, admin) ? Results.Ok() : Results.NotFound());

        // What a person is called here. Auth0 hands us the email as the name
        // for database users, so most rows arrive reading as an address.
        admin.MapPost("/users/{id}/name", (string id, NameRequest request, UserStore users) =>
            users.SetDisplayName(id, request.DisplayName) ? Results.Ok() : Results.NotFound());
    }

    // A renderer is visible to everyone (it serves everyone) but only its
    // owner or an admin may act on it.
    private static AgentKeyRecord? Own(Caller caller, AgentKeyStore keys, string id) =>
        keys.ById(id) is { } key && (caller.IsAdmin || key.OwnerUserId == caller.UserId) ? key : null;

    private static object KeyView(AgentKeyRecord r, string? ownerEmail = null, AgentLive? live = null, List<AgentLogInfo>? logs = null, bool mine = false, AccountRegistry? accounts = null) => new
    {
        r.Id, r.Name, r.Machine, Status = r.Status.ToString().ToLowerInvariant(), Role = r.Role.ToString().ToLowerInvariant(),
        r.OwnerUserId, OwnerEmail = ownerEmail, Bound = r.IsBound, Mine = mine, r.CreatedUtc, r.DecidedUtc, r.LastSeenUtc, r.LastIp, r.Note,
        ActsFor = r.ActsFor,
        ActsForRiotIds = r.ActsFor.Select(id => accounts?.ById(id)?.RiotId).OfType<string>().ToList(),
        Live = live, Logs = logs ?? [],
    };

    private static object UserView(User u, AccountRegistry accounts, AgentKeyStore keys) => new
    {
        u.Id, u.Email, u.DisplayName, u.IsAdmin, u.CreatedUtc, u.LastSeenUtc, u.InvitedUtc, u.InviteSentUtc,
        Invited = u.IsInvitedPending,
        ProviderLinked = u.ProviderUserId is not null,
        Logins = u.Logins.Select(l => l.Issuer),
        Accounts = accounts.OwnedBy(u.Id).Select(a => a.RiotId),
        Agents = keys.OwnedBy(u.Id).Count,
    };

    // Make sure the person exists at Auth0, then have Auth0 mail them. Either
    // step may fail without undoing the row; the answer says what happened so
    // the admin can act (Resend, Copy link) instead of guessing.
    private static async Task<(bool Mailed, string? Warning)> SendInviteAsync(User user, UserStore users, Auth0ManagementClient auth0, ILogger log, CancellationToken ct)
    {
        if (!auth0.Configured) return (false, "Auth0 management is not configured on this instance, so no invite was sent - the person is on the list and can sign in if their address already exists at the provider.");
        try
        {
            var providerId = user.ProviderUserId ?? await auth0.EnsureUserAsync(user.Email, user.DisplayName, ct);
            if (user.ProviderUserId is null) users.SetProviderUserId(user.Id, providerId);
            await auth0.SendPasswordSetupEmailAsync(user.Email, ct);
            users.MarkInviteSent(user.Id);
            log.LogInformation("Invite mailed to {Email} ({ProviderId})", user.Email, providerId);
            return (true, null);
        }
        catch (Auth0Exception ex)
        {
            log.LogWarning("Invite for {Email} did not complete: {Message}", user.Email, ex.Message);
            return (false, ex.Message);
        }
    }

    public sealed record AssignRequest(string? OwnerEmail, string? Role, string[]? ActsFor = null);
    public sealed record ClaimRequest(string AccountId);
    public sealed record InviteRequest(string? Email, string? DisplayName);
    public sealed record NameRequest(string? DisplayName);
}
