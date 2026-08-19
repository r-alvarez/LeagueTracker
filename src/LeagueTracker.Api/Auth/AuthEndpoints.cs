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
        var oidc = app.Configuration.GetSection("Auth:Oidc").Get<OidcOptions>();
        var oidcConfigured = oidc?.Configured is true;
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

        // Ends our session AND the provider's: with only ours cleared the next
        // "Sign in" silently comes back as the same person, which is wrong the
        // moment two people share a browser. Auth0's logout wants returnTo to be
        // one of the app's allowed logout URLs, so we go back to the site root.
        app.MapGet("/auth/logout", (HttpContext http, string? returnUrl) =>
        {
            var local = SafeReturn(returnUrl);
            if (!oidcConfigured) return Results.SignOut(new AuthenticationProperties { RedirectUri = local }, [CookieAuthenticationDefaults.AuthenticationScheme]);
            var root = $"{http.Request.Scheme}://{http.Request.Host}/";
            var provider = $"{oidc!.Authority.TrimEnd('/')}/v2/logout?client_id={Uri.EscapeDataString(oidc.ClientId)}&returnTo={Uri.EscapeDataString(root)}";
            return Results.SignOut(new AuthenticationProperties { RedirectUri = provider }, [CookieAuthenticationDefaults.AuthenticationScheme]);
        });

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
            // "Any email" means the invite gate does not apply here: the row is
            // made on the spot, as configuration would have.
            users.EnsureByEmail(email);
            var (user, _) = users.FromLogin("dev", email.ToLowerInvariant(), email, emailVerified: true, name);
            if (admin && !user.IsAdmin) { users.SetAdmin(user.Id, true); user.IsAdmin = true; }
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.SessionPrincipal(user), AuthSetup.PersistentSession());
            return returnUrl is { Length: > 0 } ? Results.Redirect(SafeReturn(returnUrl)) : Results.Ok(new { user.Id, user.Email, user.DisplayName, user.IsAdmin });
        });
    }

    private static string SafeReturn(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/";
}
