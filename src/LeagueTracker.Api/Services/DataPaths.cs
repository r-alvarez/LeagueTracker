using LeagueTracker.Api.Accounts;

namespace LeagueTracker.Api.Services;

/// Scoped: the bound account's folder. Everything file-shaped hangs off it.
public sealed class DataPaths(AccountContext account)
{
    public string DataDir => account.DataDir;

    public string GamesDir => Path.Combine(DataDir, "games");

    /// The db is a rebuildable index over the raw game files - but LP snapshots
    /// exist only at capture time, so they get mirrored to a CSV that a re-import
    /// can restore.
    public string LpLedgerCsv => Path.Combine(DataDir, "lp-history.csv");
}
