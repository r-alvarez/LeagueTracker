using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

public sealed class LpService(LeagueDbContext db, RiotApiClient riot, DataPaths paths)
{
    /// Fetches the tracked player's league entries and appends one snapshot row
    /// per ranked queue. Returns the rows written.
    public async Task<List<LpSnapshot>> TakeSnapshotAsync(string puuid, CancellationToken ct)
    {
        var entries = await riot.GetLeagueEntriesAsync(puuid, ct);
        return await WriteSnapshotRowsAsync(entries, ct);
    }

    public async Task<List<LpSnapshot>> WriteSnapshotRowsAsync(IEnumerable<LeagueEntryDto> entries, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = entries
            .Where(e => e.QueueType is RankMath.SoloQueueType or RankMath.FlexQueueType)
            .Select(e => new LpSnapshot
            {
                TimestampUtc = now,
                Queue = RankMath.QueueLabel(e.QueueType),
                Tier = e.Tier,
                Division = e.Rank,
                Lp = e.LeaguePoints,
                Wins = e.Wins,
                Losses = e.Losses,
                RankValue = RankMath.ToValue(e.Tier, e.Rank, e.LeaguePoints) ?? 0,
            })
            .ToList();
        if (rows is { Count: > 0 })
        {
            db.LpSnapshots.AddRange(rows);
            await db.SaveChangesAsync(ct);
            MirrorToCsv(rows);
        }
        return rows;
    }

    /// LP is the one thing not derivable from raw match files - mirror every
    /// snapshot to a CSV (same shape as the PowerShell ledger) so a db rebuild
    /// plus import restores the full history.
    public void MirrorToCsv(IEnumerable<LpSnapshot> rows)
    {
        Directory.CreateDirectory(paths.DataDir);
        var writeHeader = !File.Exists(paths.LpLedgerCsv);
        using var writer = File.AppendText(paths.LpLedgerCsv);
        if (writeHeader) writer.WriteLine("\"Timestamp\",\"Queue\",\"Tier\",\"Division\",\"LP\",\"Wins\",\"Losses\",\"RankValue\"");
        foreach (var r in rows)
        {
            writer.WriteLine($"\"{r.TimestampUtc:o}\",\"{r.Queue}\",\"{r.Tier}\",\"{r.Division}\",\"{r.Lp}\",\"{r.Wins}\",\"{r.Losses}\",\"{r.RankValue}\"");
        }
    }

    public Task<LpSnapshot?> GetLatestAsync(string queueLabel, CancellationToken ct) =>
        db.LpSnapshots.Where(s => s.Queue == queueLabel)
            .OrderByDescending(s => s.TimestampUtc).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

    /// LP delta for one game, from the two snapshots bracketing its end time.
    /// Only trustworthy when Riot's own win+loss counters moved by exactly one
    /// between them - otherwise several games (or a split reset) share the
    /// delta and null is returned rather than a guess.
    public async Task<(int Change, LpSnapshot Before, LpSnapshot After)?> AttributeFromBracketsAsync(
        string queueLabel, DateTime gameEndUtc, CancellationToken ct)
    {
        var before = await db.LpSnapshots
            .Where(s => s.Queue == queueLabel && s.TimestampUtc <= gameEndUtc)
            .OrderByDescending(s => s.TimestampUtc).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);
        var after = await db.LpSnapshots
            .Where(s => s.Queue == queueLabel && s.TimestampUtc > gameEndUtc)
            .OrderBy(s => s.TimestampUtc).ThenBy(s => s.Id)
            .FirstOrDefaultAsync(ct);
        if (before is null || after is null) return null;

        var gamesBetween = (after.Wins + after.Losses) - (before.Wins + before.Losses);
        if (gamesBetween != 1) return null;
        return (after.RankValue - before.RankValue, before, after);
    }
}
