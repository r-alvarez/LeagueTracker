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

    public string[] AdminList => [.. Admins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}

public sealed class OidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool Configured => Authority is { Length: > 0 } && ClientId is { Length: > 0 };
}
