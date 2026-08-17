using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Registry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var oidcConfigured = app.Configuration.GetSection("Auth:Oidc").Get<OidcOptions>()?.Configured is true;
        var devLogin = app.Environment.IsDevelopment() && app.Configuration.GetValue("Auth:DevLogin", true);

        // Browser navigation: challenge the provider, come back to where the
        // person was. Only same-site return targets - an absolute URL here
        // would make us an open redirector.
        app.MapGet("/auth/login", (string? returnUrl) =>
            oidcConfigured
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) }, [AuthSetup.OidcScheme])
                : Results.Problem(devLogin
                    ? "No identity provider configured - use /api/auth/dev-login?email=you@example.com on this Development instance"
                    : "No identity provider configured (Auth:Oidc)", statusCode: 503));

        // Ends OUR session only. The provider's own session is left alone: the
        // person may be signed into it for other reasons, and a round-trip
        // through the provider's logout is where redirect problems live.
        app.MapGet("/auth/logout", (string? returnUrl) =>
            Results.SignOut(new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) }, [CookieAuthenticationDefaults.AuthenticationScheme]));

        app.MapGet("/api/auth/me", (Caller caller, AccountRegistry accounts, IOptions<AuthOptions> auth) => Results.Ok(new
        {
            SignedIn = caller.IsUser,
            User = caller.IsUser ? new { Id = caller.UserId, caller.Email, caller.DisplayName, caller.IsAdmin } : null,
            OwnedAccountIds = caller.UserId is { } uid ? accounts.OwnedBy(uid).Select(a => a.Id).ToArray() : [],
            Agent = caller.Agent is { } agent ? new { agent.Id, agent.Name, Role = agent.Role.ToString().ToLowerInvariant(), agent.OwnerUserId } : null,
            auth.Value.PublicReads,
            LoginConfigured = oidcConfigured,
            DevLogin = devLogin,
        }));

        if (!devLogin) return;

        // Development only: sign in as any email, no provider. Registered
        // solely when the environment is Development, so it does not exist in
        // the container whatever the config says.
        app.MapGet("/api/auth/dev-login", async (HttpContext http, UserStore users, string email, string? name, bool admin = false, string? returnUrl = null) =>
        {
            var (user, _) = users.FromLogin("dev", email.ToLowerInvariant(), email, emailVerified: true, name);
            if (admin && !user.IsAdmin) { users.SetAdmin(user.Id, true); user.IsAdmin = true; }
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.SessionPrincipal(user), AuthSetup.PersistentSession());
            return returnUrl is { Length: > 0 } ? Results.Redirect(SafeReturn(returnUrl)) : Results.Ok(new { user.Id, user.Email, user.DisplayName, user.IsAdmin });
        });
    }

    private static string SafeReturn(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/";
}
