using System.Collections.Concurrent;

namespace LeagueTracker.Api.Accounts;

/// One instance of T per account, made on first use, living for the process.
/// The way process-wide state (live game, running job, render leases) stays
/// per account without every consumer learning about accounts: T is also
/// registered scoped, resolving to the bound account's instance.
public sealed class PerAccount<T>(Func<Account, T> factory) where T : class
{
    private readonly ConcurrentDictionary<string, T> _instances = new(StringComparer.OrdinalIgnoreCase);

    public T For(Account account) => _instances.GetOrAdd(account.Slug, _ => factory(account));

    public IEnumerable<(Account Account, T Instance)> All(AccountRegistry registry) =>
        registry.All.Select(a => (a, For(a)));
}

/// Scopes for work outside a request (the poller, a job kicked off from an
/// endpoint): a fresh DI scope with the account already bound, so every
/// scoped service inside resolves against that account.
public sealed class AccountScopes(IServiceScopeFactory scopes)
{
    public IServiceScope Create(Account account)
    {
        var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccountContext>().Bind(account);
        return scope;
    }
}

public static class AccountServiceCollectionExtensions
{
    /// Registers T as per-account state: singleton holder + scoped view.
    public static IServiceCollection AddPerAccount<T>(this IServiceCollection services, Func<Account, T>? factory = null) where T : class
    {
        services.AddSingleton(new PerAccount<T>(factory ?? (_ => Activator.CreateInstance<T>())));
        services.AddScoped(sp => sp.GetRequiredService<PerAccount<T>>().For(sp.GetRequiredService<AccountContext>().Current));
        return services;
    }
}

/// Binds the request's account after routing and before authorization:
/// /api/a/{region}/{slug}/... (canonical), /api/a/{slug}/... (the first
/// one-site build), /api/agent/a/{region}/{slug}/... (agents mid-update) -
/// all read from the route values the router already decoded, so there is
/// exactly one URL per account. Otherwise the Host header (legacy
/// per-account hostnames), otherwise the default account. A slug the
/// account used before a rename answers with a permanent redirect to the
/// address it has now.
public sealed class AccountBindingMiddleware(RequestDelegate next, AccountRegistry registry)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var context = http.RequestServices.GetRequiredService<AccountContext>();
        Account? account = null;

        if (http.GetRouteValue("slug") is string slug)
        {
            var region = http.GetRouteValue("region") as string;
            account = region is null ? registry.BySlug(slug) : registry.ByPath(region, slug);
            if (account is null && registry.ByPreviousSlug(slug) is { } renamed)
            {
                var path = http.Request.Path.Value ?? "";
                var stale = $"/{(region is null ? "" : region + "/")}{Uri.EscapeDataString(slug)}";
                var index = path.IndexOf(stale, StringComparison.OrdinalIgnoreCase);
                var target = index >= 0
                    ? path[..index] + $"/{renamed.RegionCode}/{Uri.EscapeDataString(renamed.Slug)}" + path[(index + stale.Length)..]
                    : $"/api/a/{renamed.RegionCode}/{Uri.EscapeDataString(renamed.Slug)}";
                http.Response.Redirect(target + http.Request.QueryString, permanent: true, preserveMethod: true);
                return;
            }
            if (account is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsync($"unknown account '{(region is null ? "" : region + "/")}{slug}'");
                return;
            }
        }
        account ??= registry.ByHost(http.Request.Host.Host) ?? registry.Default;
        context.Bind(account);
        await next(http);
    }
}
