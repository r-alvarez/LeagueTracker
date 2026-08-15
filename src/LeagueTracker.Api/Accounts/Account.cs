namespace LeagueTracker.Api.Accounts;

/// One tracked player: who they are on Riot's side and where their data
/// lives. The process hosts many; nothing about an account is global.
public sealed class Account
{
    /// URL segment: /{slug}/..., /api/a/{slug}/... - the Riot ID with '-' for
    /// '#' (op.gg style: /ImRA-87166), Riot IDs being globally unique. Blank
    /// = derived from GameName/TagLine; set explicitly only to override.
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
    /// Hostnames that mean "this account" for account-less requests - the
    /// legacy per-account hostnames, so agents and bookmarks from the
    /// three-tracker era keep working through the single process.
    public string Hosts { get; set; } = "";
    /// Shown in the account switcher; blank = GameName.
    public string DisplayName { get; set; } = "";
    public bool HideLp { get; set; }

    public string RiotId => $"{GameName}#{TagLine}";
    public string UrlSlug => Slug is { Length: > 0 } ? Slug : $"{GameName}-{TagLine}";
    public string Label => DisplayName is { Length: > 0 } ? DisplayName : GameName;
    public string[] HostList => [.. Hosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}

/// `Accounts` section. Env form: Accounts__List__0__Slug=main
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
