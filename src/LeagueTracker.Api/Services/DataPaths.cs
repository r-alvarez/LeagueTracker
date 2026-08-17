using LeagueTracker.Api.Accounts;

namespace LeagueTracker.Api.Services;

/// Scoped: the bound account's folder. Everything file-shaped hangs off it.
public sealed class DataPaths(AccountContext account)
{
    public string DataDir => account.DataDir;

    public string GamesDir => Path.Combine(DataDir, "games");

    /// The raw {matchId, match, timeline} file for a game. Match.RawPath is
    /// where the file was written at ingest - an absolute path from a previous
    /// host layout (the pre-multi-account container kept games at /data/games)
    /// or an import source folder - so the account's own games/ folder wins
    /// whenever it holds the file, and the stored path is only a fallback.
    public string? ResolveRawGame(string matchId, string? storedPath)
    {
        var local = Path.GetFullPath(Path.Combine(GamesDir, $"{matchId}.json"));
        if (File.Exists(local)) return local;
        return storedPath is { Length: > 0 } && File.Exists(storedPath) ? storedPath : null;
    }

    /// The db is a rebuildable index over the raw game files - but LP snapshots
    /// exist only at capture time, so they get mirrored to a CSV that a re-import
    /// can restore.
    public string LpLedgerCsv => Path.Combine(DataDir, "lp-history.csv");
}
