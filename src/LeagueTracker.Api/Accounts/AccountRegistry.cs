using LeagueTracker.Api.Riot;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Accounts;

/// The accounts this process serves, resolved once at startup. Legacy
/// single-account config (Riot:GameName/TagLine/DataDir, the three-container
/// era) becomes one account called "main" so an un-migrated deployment
/// behaves exactly as before.
public sealed class AccountRegistry
{
    private readonly Dictionary<string, Account> _bySlug = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Account> _byHost = new(StringComparer.OrdinalIgnoreCase);

    public AccountRegistry(IOptions<AccountsOptions> accounts, IOptions<RiotOptions> riot, IWebHostEnvironment env)
    {
        var options = accounts.Value;
        var legacy = riot.Value;
        var list = options.List is { Count: > 0 } ? options.List : [LegacyAccount(legacy)];
        var root = options.DataRoot is { Length: > 0 } r ? Rooted(r, env) : null;

        foreach (var account in list)
        {
            if (account.Slug is not { Length: > 0 }) throw new InvalidOperationException("Every account needs a Slug");
            if (account.DataDir is not { Length: > 0 })
            {
                account.DataDir = root is not null
                    ? Path.Combine(root, account.Slug)
                    : throw new InvalidOperationException($"Account {account.Slug}: set DataDir or Accounts:DataRoot");
            }
            account.DataDir = Rooted(account.DataDir, env);
            Directory.CreateDirectory(account.DataDir);
            _bySlug[account.Slug] = account;
            foreach (var host in account.HostList) _byHost[host] = account;
        }
        All = [.. list];
        Default = (options.Default is { Length: > 0 } d && _bySlug.TryGetValue(d, out var chosen)) ? chosen : All[0];
    }

    public IReadOnlyList<Account> All { get; }
    public Account Default { get; }

    public Account? BySlug(string? slug) => slug is { Length: > 0 } && _bySlug.TryGetValue(slug, out var a) ? a : null;
    public Account? ByHost(string? host) => host is { Length: > 0 } && _byHost.TryGetValue(host, out var a) ? a : null;

    private static Account LegacyAccount(RiotOptions legacy) => new()
    {
        Slug = "main",
        GameName = legacy.GameName,
        TagLine = legacy.TagLine,
        Region = legacy.Region,
        Platform = legacy.Platform,
        DataDir = legacy.DataDir,
        HideLp = legacy.HideLp,
    };

    private static string Rooted(string path, IWebHostEnvironment env) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}

/// The account a unit of work is about. Scoped: an HTTP request gets it
/// from the route ({slug}) or the Host header; background loops set it
/// explicitly per account scope. Anything account-bound (paths, the db,
/// Riot routing) reads through here instead of global options.
public sealed class AccountContext(AccountRegistry registry)
{
    private Account? _current;

    public Account Current => _current ?? throw new InvalidOperationException("No account bound to this scope");
    public bool IsBound => _current is not null;

    public void Bind(Account account) => _current = account;

    public string Slug => Current.Slug;
    public string DataDir => Current.DataDir;
    public AccountRegistry Registry => registry;
}
