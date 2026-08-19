namespace LeagueTracker.Api.Auth;

// `Auth` section. Env form: Auth__Oidc__Authority=https://tenant.eu.auth0.com/
// Auth__Oidc__ClientId=... Auth__Oidc__ClientSecret=... Auth__Admins=a@b.c,d@e.f
public sealed class AuthOptions
{
    public OidcOptions Oidc { get; set; } = new();
    // Comma-separated emails that are admins (seeded as users at boot).
    public string Admins { get; set; } = "";
    // Transitional: while Cloudflare Access still walls the site, reads of
    // Riot-derived data need a signed-in principal; the public launch flips
    // this to true and anonymous reads are allowed in-process.
    public bool PublicReads { get; set; }
    // Development only: /api/auth/dev-login signs in as any email. Never
    // honoured outside the Development environment, whatever this says.
    public bool DevLogin { get; set; } = true;
    // A sign-in from an identity nobody here knows (no login link, no user
    // with that verified email, no invite) is refused instead of creating a
    // user - so "invite-only" holds in the app, not just in the tenant's
    // sign-up switch. Off = the pre-invites behaviour, first login creates.
    public bool InviteOnly { get; set; } = true;
    public ManagementOptions Management { get; set; } = new();

    public string[] AdminList => [.. Admins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}

public sealed class OidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool Configured => Authority is { Length: > 0 } && ClientId is { Length: > 0 };
}

// The Machine-to-Machine application that lets the tracker create people at
// Auth0 (invites). Separate credentials from the login client on purpose:
// the login client must never hold user-management scopes. Env form:
// Auth__Management__ClientId / Auth__Management__ClientSecret.
public sealed class ManagementOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Connection { get; set; } = "Username-Password-Authentication";
    public bool Configured => ClientId is { Length: > 0 } && ClientSecret is { Length: > 0 };
}
