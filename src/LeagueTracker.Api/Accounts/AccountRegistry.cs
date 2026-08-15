using System.Text.Json;
using LeagueTracker.Api.Riot;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Accounts;

/// The accounts this process serves: the configured seed (Accounts:List, or
/// legacy Riot:GameName as one account) plus whatever was added at runtime
/// through the site (persisted in <DataRoot>/accounts.json, so a redeploy
/// keeps them). Config wins over the file for the same Riot ID.
public sealed class AccountRegistry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly List<Account> _all = [];
    private readonly Dictionary<string, Account> _bySlug = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Account> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _root;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AccountRegistry> _log;

    public AccountRegistry(IOptions<AccountsOptions> accounts, IOptions<RiotOptions> riot, IWebHostEnvironment env, ILogger<AccountRegistry> log)
    {
        _env = env;
        _log = log;
        var options = accounts.Value;
        _root = options.DataRoot is { Length: > 0 } r ? Rooted(r) : null;

        var seed = options.List is { Count: > 0 } ? options.List : [LegacyAccount(riot.Value)];
        foreach (var account in seed) Register(account, fromConfig: true);
        foreach (var account in LoadAdded()) Register(account, fromConfig: false);

        Default = (options.Default is { Length: > 0 } d && BySlug(d) is { } chosen) ? chosen : _all[0];
    }

    public IReadOnlyList<Account> All { get { lock (_gate) return [.. _all]; } }
    public Account Default { get; }
    public bool CanAdd => _root is not null;

    public Account? BySlug(string? slug) { lock (_gate) return slug is { Length: > 0 } && _bySlug.TryGetValue(slug, out var a) ? a : null; }
    public Account? ByHost(string? host) { lock (_gate) return host is { Length: > 0 } && _byHost.TryGetValue(host, out var a) ? a : null; }

    /// Region code + slug ("euw", "ImRA-87166") - the canonical address. The
    /// slug alone still resolves (Riot IDs are unique), the region is there
    /// because that is how the world writes these URLs.
    public Account? ByPath(string? region, string? slug)
    {
        var account = BySlug(slug);
        if (account is null) return null;
        return region is null || account.RegionCode.Equals(region, StringComparison.OrdinalIgnoreCase) ? account : null;
    }

    /// Runtime add (the site's "add account" box). The caller has already
    /// resolved the Riot ID; this gives it a folder and remembers it.
    public Account Add(string gameName, string tagLine, string platform, string? displayName)
    {
        if (_root is null) throw new InvalidOperationException("Accounts:DataRoot is not set - accounts can only come from config");
        var entry = Platforms.ByPlatform(platform) ?? throw new ArgumentException($"unknown platform '{platform}'");
        var account = new Account
        {
            GameName = gameName,
            TagLine = tagLine,
            Platform = entry.Platform,
            Region = entry.Region,
            DisplayName = displayName ?? "",
        };
        lock (_gate)
        {
            if (_bySlug.ContainsKey(account.UrlSlug)) throw new InvalidOperationException($"{account.RiotId} is already tracked");
            Register(account, fromConfig: false);
            SaveAdded();
        }
        _log.LogInformation("Account added: {RiotId} ({Platform}) at {Dir}", account.RiotId, account.Platform, account.DataDir);
        return account;
    }

    /// Stops tracking; the data folder stays on disk (deleting history is a
    /// human's decision, on the NAS). Config-seeded accounts cannot be
    /// removed here - they come back at the next start.
    public bool Remove(string slug)
    {
        lock (_gate)
        {
            if (BySlug(slug) is not { } account || account.FromConfig) return false;
            _all.Remove(account);
            _bySlug.Remove(account.Slug);
            foreach (var host in account.HostList) _byHost.Remove(host);
            SaveAdded();
            _log.LogInformation("Account removed from tracking: {RiotId} (folder kept: {Dir})", account.RiotId, account.DataDir);
            return true;
        }
    }

    private void Register(Account account, bool fromConfig)
    {
        if (account.GameName is not { Length: > 0 } || account.TagLine is not { Length: > 0 }) throw new InvalidOperationException("Every account needs GameName and TagLine");
        account.Slug = account.UrlSlug;
        account.FromConfig = fromConfig;
        if (_bySlug.ContainsKey(account.Slug))
        {
            if (!fromConfig) { _log.LogWarning("accounts.json entry {RiotId} duplicates a configured account - ignored", account.RiotId); return; }
            throw new InvalidOperationException($"Account {account.RiotId} is configured twice");
        }
        if (account.DataDir is not { Length: > 0 })
        {
            account.DataDir = _root is not null
                ? Path.Combine(_root, account.Slug)
                : throw new InvalidOperationException($"Account {account.Slug}: set DataDir or Accounts:DataRoot");
        }
        account.DataDir = Rooted(account.DataDir);
        Directory.CreateDirectory(account.DataDir);
        _all.Add(account);
        _bySlug[account.Slug] = account;
        foreach (var host in account.HostList) _byHost[host] = account;
    }

    private string AddedPath => Path.Combine(_root!, "accounts.json");

    private List<Account> LoadAdded()
    {
        if (_root is null || !File.Exists(AddedPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(AddedPath), Json) ?? [];
        }
        catch (Exception ex)
        {
            _log.LogError("accounts.json is unreadable ({Message}) - runtime-added accounts are not loaded", ex.Message);
            return [];
        }
    }

    private void SaveAdded()
    {
        var added = _all.Where(a => !a.FromConfig).Select(a => new Account
        {
            GameName = a.GameName, TagLine = a.TagLine, Platform = a.Platform, Region = a.Region,
            DisplayName = a.DisplayName, HideLp = a.HideLp, Hosts = a.Hosts,
        }).ToList();
        var tmp = AddedPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(added, Json));
        File.Move(tmp, AddedPath, overwrite: true);
    }

    private static Account LegacyAccount(RiotOptions legacy) => new()
    {
        GameName = legacy.GameName,
        TagLine = legacy.TagLine,
        Region = legacy.Region,
        Platform = legacy.Platform,
        DataDir = legacy.DataDir,
        HideLp = legacy.HideLp,
    };

    private string Rooted(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(_env.ContentRootPath, path);
}

/// The account a unit of work is about. Scoped: an HTTP request gets it
/// from the route ({region}/{slug}) or the Host header; background loops set
/// it explicitly per account scope. Anything account-bound (paths, the db,
/// Riot routing) reads through here instead of global options.
public sealed class AccountContext(AccountRegistry registry)
{
    private Account? _current;

    public Account Current => _current ?? throw new InvalidOperationException("No account bound to this scope");
    public bool IsBound => _current is not null;

    public void Bind(Account account) => _current = account;

    public string Slug => Current.Slug;
    /// "euw/ImRA-87166" as it goes into a URL (game names may hold spaces/unicode).
    public string UrlSegment => $"{Current.RegionCode}/{Uri.EscapeDataString(Current.Slug)}";
    public string DataDir => Current.DataDir;
    public AccountRegistry Registry => registry;
}
