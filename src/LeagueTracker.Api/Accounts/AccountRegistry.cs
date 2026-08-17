using System.Text.Json;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Accounts;

// The accounts this process serves, held in memory (the binding middleware
// asks on every request) and written through to registry.db. Configuration
// (Accounts:List, or legacy Riot:GameName as one account) is re-applied at
// every boot and wins for the accounts it names; everything else - accounts
// added from the site, owners assigned, renames - lives in the registry.
// A first boot on this build imports the old accounts.json once.
public sealed class AccountRegistry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly List<Account> _all = [];
    private readonly Dictionary<string, Account> _bySlug = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Account> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Account> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _root;
    private readonly IWebHostEnvironment _env;
    private readonly RegistryDatabase _registry;
    private readonly ILogger<AccountRegistry> _log;

    public AccountRegistry(IOptions<AccountsOptions> accounts, IOptions<RiotOptions> riot, RegistryDatabase registry, IWebHostEnvironment env, ILogger<AccountRegistry> log)
    {
        _env = env;
        _log = log;
        _registry = registry;
        registry.EnsureCreated(log);
        var options = accounts.Value;
        _root = options.DataRoot is { Length: > 0 } r ? Rooted(r) : null;

        using var db = registry.Open();
        var stored = db.Accounts.AsNoTracking().ToList();
        if (stored is []) stored = ImportLegacyJson(db);

        var seed = options.List is { Count: > 0 } ? options.List : [LegacyAccount(riot.Value)];
        foreach (var configured in seed) stored = ApplyConfig(db, stored, configured);
        foreach (var account in stored.OrderBy(a => a.CreatedUtc)) Register(account);

        Default = (options.Default is { Length: > 0 } d && BySlug(d) is { } chosen) ? chosen : _all[0];
    }

    public IReadOnlyList<Account> All { get { lock (_gate) return [.. _all]; } }
    public Account Default { get; }
    public bool CanAdd => _root is not null;

    public Account? BySlug(string? slug) { lock (_gate) return slug is { Length: > 0 } && _bySlug.TryGetValue(slug, out var a) ? a : null; }
    public Account? ById(string? id) { lock (_gate) return id is { Length: > 0 } && _byId.TryGetValue(id, out var a) ? a : null; }
    public Account? ByHost(string? host) { lock (_gate) return host is { Length: > 0 } && _byHost.TryGetValue(host, out var a) ? a : null; }
    public Account? ByPuuid(string? puuid) { lock (_gate) return puuid is { Length: > 0 } ? _all.FirstOrDefault(a => a.Puuid == puuid) : null; }

    // A slug this account used to answer to - the 301 source. Case-insensitive
    // like every slug lookup.
    public Account? ByPreviousSlug(string? slug)
    {
        if (slug is not { Length: > 0 }) return null;
        lock (_gate) return _all.FirstOrDefault(a => a.PreviousSlugList.Any(p => p.Equals(slug, StringComparison.OrdinalIgnoreCase)));
    }

    // Region code + slug ("euw", "ImRA-87166") - the canonical address. The
    // slug alone still resolves (Riot IDs are unique), the region is there
    // because that is how the world writes these URLs.
    public Account? ByPath(string? region, string? slug)
    {
        var account = BySlug(slug);
        if (account is null) return null;
        return region is null || account.RegionCode.Equals(region, StringComparison.OrdinalIgnoreCase) ? account : null;
    }

    public IReadOnlyList<Account> OwnedBy(string userId) { lock (_gate) return [.. _all.Where(a => a.OwnerUserId == userId)]; }

    // Runtime add (the site's "add account" box). The caller has already
    // resolved the Riot ID; this gives it a folder and remembers it. Owner is
    // null: an unowned public profile until someone claims it.
    public Account Add(string gameName, string tagLine, string platform, string? displayName, string? puuid)
    {
        if (_root is null) throw new InvalidOperationException("Accounts:DataRoot is not set - accounts can only come from config");
        var entry = Platforms.ByPlatform(platform) ?? throw new ArgumentException($"unknown platform '{platform}'");
        var account = new Account
        {
            Id = Ids.New(),
            GameName = gameName,
            TagLine = tagLine,
            Platform = entry.Platform,
            Region = entry.Region,
            DisplayName = displayName ?? "",
            Puuid = puuid,
            CreatedUtc = DateTime.UtcNow,
        };
        lock (_gate)
        {
            if (_bySlug.ContainsKey(account.UrlSlug)) throw new InvalidOperationException($"{account.RiotId} is already tracked");
            // The folder is named by the surrogate id, not the Riot ID: a rename
            // must never move data, so the name must not be something Riot changes.
            account.DataDir = Path.Combine(_root, account.Id);
            Register(account);
            Persist(account);
        }
        _log.LogInformation("Account added: {RiotId} ({Platform}) at {Dir}", account.RiotId, account.Platform, account.DataDir);
        return account;
    }

    // Stops tracking; the data folder stays on disk (deleting history is a
    // human's decision, on the NAS). Config-seeded accounts cannot be
    // removed here - they come back at the next start.
    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (ById(id) is not { } account || account.FromConfig) return false;
            _all.Remove(account);
            _bySlug.Remove(account.Slug);
            _byId.Remove(account.Id);
            foreach (var host in account.HostList) _byHost.Remove(host);
            using var db = _registry.Open();
            db.Accounts.Where(a => a.Id == account.Id).ExecuteDelete();
            _log.LogInformation("Account removed from tracking: {RiotId} (folder kept: {Dir})", account.RiotId, account.DataDir);
            return true;
        }
    }

    // Any change to an account's own fields (puuid learned, owner set, a
    // setting flipped) goes through here so memory and registry agree.
    public void Update(Account account, Action<Account> change)
    {
        lock (_gate)
        {
            change(account);
            Persist(account);
        }
    }

    // A rename on Riot's side: the old slug becomes a 301 source, the new one
    // the address. The folder never moves.
    public void Rename(Account account, string gameName, string tagLine)
    {
        string oldSlug;
        lock (_gate)
        {
            oldSlug = account.Slug;
            _bySlug.Remove(oldSlug);
            account.GameName = gameName;
            account.TagLine = tagLine;
            account.Slug = account.UrlSlug;
            var previous = account.PreviousSlugList.Where(p => !p.Equals(account.Slug, StringComparison.OrdinalIgnoreCase)).Append(oldSlug).Distinct(StringComparer.OrdinalIgnoreCase);
            account.PreviousSlugs = string.Join(',', previous);
            _bySlug[account.Slug] = account;
            Persist(account);
        }
        _log.LogInformation("Account renamed: {Old} -> {New} (folder unchanged)", oldSlug, account.RiotId);
    }

    private void Register(Account account)
    {
        if (account.GameName is not { Length: > 0 } || account.TagLine is not { Length: > 0 }) throw new InvalidOperationException("Every account needs GameName and TagLine");
        account.Slug = account.UrlSlug;
        if (_bySlug.ContainsKey(account.Slug)) throw new InvalidOperationException($"Account {account.RiotId} is registered twice");
        account.DataDir = Rooted(account.DataDir);
        Directory.CreateDirectory(account.DataDir);
        _all.Add(account);
        _bySlug[account.Slug] = account;
        _byId[account.Id] = account;
        foreach (var host in account.HostList) _byHost[host] = account;
    }

    private void Persist(Account account)
    {
        using var db = _registry.Open();
        var exists = db.Accounts.AsNoTracking().Any(a => a.Id == account.Id);
        if (exists) db.Accounts.Update(account); else db.Accounts.Add(account);
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    // Configuration is applied over the stored row it corresponds to (matched
    // by DataDir - the one thing a config account keeps through a rename -
    // then by Riot ID) so a redeploy re-asserts the compose without ever
    // creating a duplicate or moving a folder.
    private List<Account> ApplyConfig(RegistryDbContext db, List<Account> stored, Account configured)
    {
        if (configured.GameName is not { Length: > 0 } || configured.TagLine is not { Length: > 0 }) throw new InvalidOperationException("Every configured account needs GameName and TagLine");
        var dataDir = configured.DataDir is { Length: > 0 } dir ? Rooted(dir)
            : _root is not null ? Path.Combine(_root, configured.UrlSlug)
            : throw new InvalidOperationException($"Account {configured.UrlSlug}: set DataDir or Accounts:DataRoot");
        var match = stored.FirstOrDefault(a => PathsEqual(a.DataDir, dataDir))
            ?? stored.FirstOrDefault(a => a.UrlSlug.Equals(configured.UrlSlug, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            match = new Account { Id = Ids.New(), CreatedUtc = DateTime.UtcNow };
            stored.Add(match);
            _log.LogInformation("Account {RiotId} from configuration registered as {Id}", configured.RiotId, match.Id);
        }
        // Config wins for what config states; a rename learned from Riot stays
        // unless the compose was updated to the new name too (then they agree).
        if (!match.RiotId.Equals(configured.RiotId, StringComparison.OrdinalIgnoreCase) && match.GameName is { Length: > 0 })
        {
            _log.LogInformation("Account {Id}: configuration says {Config}, registry says {Stored} - keeping the registry's (a rename learned from Riot)", match.Id, configured.RiotId, match.RiotId);
        }
        else
        {
            match.GameName = configured.GameName;
            match.TagLine = configured.TagLine;
        }
        match.Slug = match.UrlSlug;
        match.Region = configured.Region;
        match.Platform = configured.Platform;
        match.DataDir = dataDir;
        match.Hosts = configured.Hosts;
        match.DisplayName = configured.DisplayName;
        match.HideLp = configured.HideLp;
        match.FromConfig = true;
        match.Owner = configured.Owner;
        if (configured.Puuid is { Length: > 0 }) match.Puuid = configured.Puuid;
        var tracked = db.Accounts.Local.FirstOrDefault(a => a.Id == match.Id);
        if (tracked is null && db.Accounts.AsNoTracking().Any(a => a.Id == match.Id)) db.Accounts.Update(match);
        else if (tracked is null) db.Accounts.Add(match);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return stored;
    }

    // Everything the site added under the old build lived in accounts.json;
    // it comes across once and the file is kept as *.imported.
    private List<Account> ImportLegacyJson(RegistryDbContext db)
    {
        if (_root is null) return [];
        var path = Path.Combine(_root, "accounts.json");
        if (!File.Exists(path)) return [];
        List<Account> added;
        try
        {
            added = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception ex)
        {
            _log.LogError("accounts.json is unreadable ({Message}) - nothing imported; fix or remove the file", ex.Message);
            return [];
        }
        List<Account> imported = [];
        foreach (var a in added)
        {
            if (a.GameName is not { Length: > 0 } || a.TagLine is not { Length: > 0 }) continue;
            var account = new Account
            {
                Id = Ids.New(),
                GameName = a.GameName, TagLine = a.TagLine, Platform = a.Platform, Region = a.Region,
                DisplayName = a.DisplayName, HideLp = a.HideLp, Hosts = a.Hosts,
                DataDir = a.DataDir is { Length: > 0 } dir ? Rooted(dir) : Path.Combine(_root, a.UrlSlug),
                CreatedUtc = DateTime.UtcNow,
            };
            account.Slug = account.UrlSlug;
            db.Accounts.Add(account);
            imported.Add(account);
        }
        db.SaveChanges();
        db.ChangeTracker.Clear();
        File.Move(path, path + ".imported", overwrite: true);
        _log.LogInformation("Imported {Count} account(s) from accounts.json into registry.db (file kept as accounts.json.imported)", imported.Count);
        return imported;
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

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private string Rooted(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(_env.ContentRootPath, path);
}

// The account a unit of work is about. Scoped: an HTTP request gets it
// from the route ({region}/{slug}) or the Host header; background loops set
// it explicitly per account scope. Anything account-bound (paths, the db,
// Riot routing) reads through here instead of global options.
public sealed class AccountContext(AccountRegistry registry)
{
    private Account? _current;

    public Account Current => _current ?? throw new InvalidOperationException("No account bound to this scope");
    public bool IsBound => _current is not null;

    public void Bind(Account account) => _current = account;

    public string Slug => Current.Slug;
    // "euw/ImRA-87166" as it goes into a URL (game names may hold spaces/unicode).
    public string UrlSegment => $"{Current.RegionCode}/{Uri.EscapeDataString(Current.Slug)}";
    public string DataDir => Current.DataDir;
    public AccountRegistry Registry => registry;
}
