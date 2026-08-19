namespace LeagueTracker.Api.Accounts;

/// One tracked player: who they are on Riot's side and where their data
/// lives. The process hosts many; nothing about an account is global.
public sealed class Account
{
    // The app's own key: minted once, never derived from anything Riot can
    // rename or re-encrypt. Folders, ownership, agent scopes all hang off it.
    public string Id { get; set; } = "";
    // Riot's player id as this deployment's API key holder sees it. Unique,
    // refreshable (a key-holder change re-resolves it), deliberately not the
    // primary key. Null until resolved for the first time.
    public string? Puuid { get; set; }
    // The User who proved this is their Riot account; null = an unowned
    // public profile (Riot-derived data only, nothing to mutate).
    public string? OwnerUserId { get; set; }
    // Owner setting: recordings, clips and renders on the public profile.
    // Off by default - a stranger's footage is private until they say so.
    public bool MediaPublic { get; set; }
    // Riot IDs this account answered to before a rename ("Old-TAG,Older-TAG"),
    // so old links 301 to the current address instead of dying.
    public string PreviousSlugs { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    // Config only (Accounts__List__N__Owner=<email>): resolved to OwnerUserId at
    // boot. Not persisted as such.
    [System.Text.Json.Serialization.JsonIgnore]
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string Owner { get; set; } = "";

    /// URL segment: /{slug}/..., /api/a/{slug}/... - the Riot ID with '-' for
    /// '#' (op.gg style: /ImRA-87166), Riot IDs being globally unique. Always
    /// derived from GameName/TagLine (stored so the registry can index it).
    public string Slug { get; set; } = "";
    public string GameName { get; set; } = "";
    public string TagLine { get; set; } = "";
    /// Regional routing (account-v1, match-v5): americas | europe | asia | sea
    public string Region { get; set; } = "europe";
    /// Platform routing (league-v4): euw1, eun1, na1, ...
    public string Platform { get; set; } = "euw1";
    /// Blank = <Accounts:DataRoot>/<Slug>. The three legacy trackers' folders
    /// (main/alt/ben) plug straight in - nothing moves.
    public string DataDir { get; set; } = "";
    /// The three-container era's per-account hostnames. No longer bound to
    /// anything; the column stays because registry.db has it NOT NULL and
    /// SQLite cannot drop a constraint without rebuilding the table.
    public string Hosts { get; set; } = "";
    /// Shown in the account switcher; blank = GameName.
    public string DisplayName { get; set; } = "";
    public bool HideLp { get; set; }

    /// Seeded from configuration (cannot be removed at runtime) vs added
    /// through the site (accounts.json).
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FromConfig { get; set; }

    public string RiotId => $"{GameName}#{TagLine}";
    public string UrlSlug => $"{GameName}-{TagLine}";
    /// URL region: euw, eune, na... derived from Platform.
    public string RegionCode => Platforms.CodeFor(Platform);
    /// "euw/ImRA-87166" - the canonical address.
    public string UrlPath => $"{RegionCode}/{UrlSlug}";
    public string Label => DisplayName is { Length: > 0 } ? DisplayName : GameName;
    public string[] PreviousSlugList => [.. PreviousSlugs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    public bool IsOwned => OwnerUserId is { Length: > 0 };
}

/// `Accounts` section. Env form: Accounts__List__0__GameName=ImRA
/// Accounts__List__0__GameName=... Accounts__DataRoot=/data
public sealed class AccountsOptions
{
    public List<Account> List { get; set; } = [];
    /// Parent folder of the per-account data dirs.
    public string DataRoot { get; set; } = "";
    /// The account "/" shows and account-less requests without a mapped host
    /// fall back to. Blank = the first.
    public string Default { get; set; } = "";
}
