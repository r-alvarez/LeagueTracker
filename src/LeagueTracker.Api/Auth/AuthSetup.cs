using System.Security.Claims;
using LeagueTracker.Api.Registry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LeagueTracker.Api.Auth;

public static class AuthSetup
{
    public const string SmartScheme = "Smart";
    public const string OidcScheme = "oidc";
    public const string CookieName = "lt.session";
    // A cross-site form can post to us with the session cookie attached; it
    // cannot add a custom header. The SPA's fetch wrapper always sends this,
    // so its absence on a cookie-authenticated write is a forgery.
    public const string RequestedWithHeader = "X-Requested-With";
    public const string RequestedWithValue = "LeagueTracker";

    // The app is a standard OIDC relying party with its own session: the
    // provider (Auth0 today) vouches once, the cookie carries OUR identity
    // (user id, email, admin) from then on. Agents never see a cookie - the
    // key header selects their scheme.
    public static void AddTrackerAuth(this WebApplicationBuilder builder)
    {
        var oidc = builder.Configuration.GetSection("Auth:Oidc").Get<OidcOptions>() ?? new OidcOptions();

        var auth = builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = SmartScheme;
            o.DefaultChallengeScheme = SmartScheme;
        });
        auth.AddPolicyScheme(SmartScheme, "cookie or agent key", o =>
            o.ForwardDefaultSelector = http =>
                http.Request.Headers.ContainsKey(AgentKeyAuthenticationHandler.HeaderName)
                    ? AgentKeyAuthenticationHandler.Scheme
                    : CookieAuthenticationDefaults.AuthenticationScheme);
        auth.AddScheme<AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentKeyAuthenticationHandler.Scheme, null);
        auth.AddCookie(o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // localhost review runs on http
            o.ExpireTimeSpan = TimeSpan.FromDays(30);
            o.SlidingExpiration = true;
            o.LoginPath = "/auth/login";
            o.LogoutPath = "/auth/logout";
            // API callers get status codes, never a redirect to a login page.
            o.Events.OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        if (oidc.Configured)
        {
            auth.AddOpenIdConnect(OidcScheme, o =>
            {
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.Authority = oidc.Authority;
                o.ClientId = oidc.ClientId;
                o.ClientSecret = oidc.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;
                o.SaveTokens = false;
                o.CallbackPath = "/auth/callback";
                o.SignedOutCallbackPath = "/auth/signout-callback";
                o.MapInboundClaims = false;
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
                o.TokenValidationParameters.NameClaimType = "name";
                o.Events.OnTokenValidated = ctx =>
                {
                    var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
                    var incoming = ctx.Principal!;
                    var issuer = ctx.SecurityToken.Issuer;
                    var subject = incoming.FindFirstValue("sub") ?? throw new InvalidOperationException("id_token without sub");
                    var email = incoming.FindFirstValue("email");
                    var verified = string.Equals(incoming.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
                    var name = incoming.FindFirstValue("name") ?? incoming.FindFirstValue("nickname");
                    var (user, _) = users.FromLogin(issuer, subject, email, verified, name);
                    ctx.Principal = SessionPrincipal(user);
                    return Task.CompletedTask;
                };
            });
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<Caller>();
        builder.Services.AddSingleton<IAuthorizationHandler, AccessHandler>();
        builder.Services.AddAuthorization(Policies.AddAll);
    }

    // What the session cookie carries: our user, not the provider's.
    public static ClaimsPrincipal SessionPrincipal(User user)
    {
        List<Claim> claims =
        [
            new(TrackerClaims.UserId, user.Id),
            new(TrackerClaims.Email, user.Email),
            new(TrackerClaims.Name, user.DisplayName is { Length: > 0 } ? user.DisplayName : user.Email),
        ];
        if (user.IsAdmin) claims.Add(new(TrackerClaims.Admin, "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, TrackerClaims.Name, ClaimTypes.Role));
    }

    // Cookie-authenticated writes must carry the SPA's header; agent requests
    // authenticate by header and are exempt, as are the auth endpoints the
    // browser drives by navigation.
    public static IApplicationBuilder UseCsrfGuard(this IApplicationBuilder app) => app.Use((http, next) =>
    {
        var unsafeMethod = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method);
        var viaCookie = http.User.Identity?.IsAuthenticated is true && http.User.Identity.AuthenticationType is CookieAuthenticationDefaults.AuthenticationScheme;
        if (unsafeMethod && viaCookie && http.Request.Path.StartsWithSegments("/api")
            && http.Request.Headers[RequestedWithHeader].FirstOrDefault() != RequestedWithValue)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            http.Response.ContentType = "application/json";
            return http.Response.WriteAsync("{\"error\":\"missing X-Requested-With header\"}");
        }
        return next(http);
    });
}
