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

/// Binds the request's account before any scoped service is built:
/// /api/a/{region}/{slug}/... (canonical), /api/a/{slug}/... (the first
/// one-site build; kept for agents mid-update); otherwise the Host header
/// (legacy per-account hostnames); otherwise the default account.
public sealed class AccountBindingMiddleware(RequestDelegate next, AccountRegistry registry)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var context = http.RequestServices.GetRequiredService<AccountContext>();
        var path = http.Request.Path.Value ?? "";
        Account? account = null;

        // /api/a/... for humans (behind Access), /api/agent/a/... for keyed
        // agents (the Access-bypassed slice) - same account addressing.
        var prefix = path.StartsWith("/api/a/", StringComparison.OrdinalIgnoreCase) ? "/api/a/"
            : path.StartsWith("/api/agent/a/", StringComparison.OrdinalIgnoreCase) ? "/api/agent/a/"
            : null;
        if (prefix is not null)
        {
            var parts = path[prefix.Length..].Split('/', 3);
            var first = Uri.UnescapeDataString(parts[0]);
            if (Platforms.ByCode(first) is not null && parts.Length > 1)
            {
                var slug = Uri.UnescapeDataString(parts[1]);
                account = registry.ByPath(first, slug);
                if (account is null)
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    await http.Response.WriteAsync($"unknown account '{first}/{slug}'");
                    return;
                }
            }
            else
            {
                account = registry.BySlug(first);
                if (account is null)
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    await http.Response.WriteAsync($"unknown account '{first}'");
                    return;
                }
            }
        }
        account ??= registry.ByHost(http.Request.Host.Host) ?? registry.Default;
        context.Bind(account);
        await next(http);
    }
}
