using LeagueTracker.Api.Riot;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

public sealed class DataPaths(IOptions<RiotOptions> options, IWebHostEnvironment env)
{
    public string DataDir { get; } = Path.IsPathRooted(options.Value.DataDir)
        ? options.Value.DataDir
        : Path.Combine(env.ContentRootPath, options.Value.DataDir);

    public string GamesDir => Path.Combine(DataDir, "games");

    /// The db is a rebuildable index over the raw game files - but LP snapshots
    /// exist only at capture time, so they get mirrored to a CSV that a re-import
    /// can restore.
    public string LpLedgerCsv => Path.Combine(DataDir, "lp-history.csv");
}
