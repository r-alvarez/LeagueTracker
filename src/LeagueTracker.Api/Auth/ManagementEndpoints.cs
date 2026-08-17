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

        me.MapGet("/agents", (Caller caller, AgentKeyStore keys, AgentRegistry agents, UserStore users) =>
        {
            // An admin sees every machine and whose it is; an owner sees theirs.
            var emails = caller.IsAdmin ? users.All().ToDictionary(u => u.Id, u => u.Email) : [];
            return Results.Ok(new
            {
                Keys = (caller.IsAdmin ? keys.All : keys.OwnedBy(caller.UserId!)).Select(k => KeyView(k, k.OwnerUserId is { } o && emails.TryGetValue(o, out var e) ? e : null)),
                Live = agents.SnapshotFor(caller.UserId, caller.IsAdmin),
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

        admin.MapGet("/users", (UserStore users, AccountRegistry accounts, AgentKeyStore keys) => Results.Ok(users.All().Select(u => new
        {
            u.Id, u.Email, u.DisplayName, u.IsAdmin, u.CreatedUtc, u.LastSeenUtc,
            Logins = u.Logins.Select(l => l.Issuer),
            Accounts = accounts.OwnedBy(u.Id).Select(a => a.RiotId),
            Agents = keys.OwnedBy(u.Id).Count,
        })));

        admin.MapGet("/agents", (AgentKeyStore keys, UserStore users, AgentRegistry agents) =>
        {
            var byId = users.All().ToDictionary(u => u.Id, u => u.Email);
            return Results.Ok(new
            {
                Keys = keys.All.Select(k => KeyView(k, k.OwnerUserId is { } o && byId.TryGetValue(o, out var e) ? e : null)),
                Live = agents.SnapshotFor(null, admin: true),
            });
        });

        // The owner and role of a machine, by hand: the four keys from before
        // ownership existed, or a friend who enrolled without a code.
        admin.MapPost("/agents/{id}/assign", (string id, AssignRequest request, AgentKeyStore keys, UserStore users) =>
        {
            string? ownerId = null;
            if (request.OwnerEmail is { Length: > 0 } email)
            {
                if (users.ByEmail(email) is not { } owner) return Results.NotFound(new { error = $"no user with email {email} - they must sign in once first, or be named in configuration" });
                ownerId = owner.Id;
            }
            AgentRole? role = request.Role is { Length: > 0 } r && Enum.TryParse<AgentRole>(r, ignoreCase: true, out var parsed) ? parsed : null;
            return keys.Assign(id, ownerId, role) ? Results.Ok() : Results.NotFound();
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
    }

    private static AgentKeyRecord? Own(Caller caller, AgentKeyStore keys, string id) =>
        keys.ById(id) is { } key && (caller.IsAdmin || key.OwnerUserId == caller.UserId) ? key : null;

    private static object KeyView(AgentKeyRecord r, string? ownerEmail = null) => new
    {
        r.Id, r.Name, r.Machine, Status = r.Status.ToString().ToLowerInvariant(), Role = r.Role.ToString().ToLowerInvariant(),
        r.OwnerUserId, OwnerEmail = ownerEmail, Bound = r.IsBound, r.CreatedUtc, r.DecidedUtc, r.LastSeenUtc, r.LastIp, r.Note,
    };

    public sealed record AssignRequest(string? OwnerEmail, string? Role);
    public sealed record ClaimRequest(string AccountId);
}
