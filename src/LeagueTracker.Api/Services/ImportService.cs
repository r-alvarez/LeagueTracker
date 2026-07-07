using System.Globalization;
using System.Text.Json;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// One-time ingestion of the PowerShell tooling's output: export-*/live folders
/// (games\*.json wrappers) plus the lp-history.csv ledger and lp-per-game.csv,
/// so history built up by the scripts carries straight into the db.
public sealed class ImportService(
    LeagueDbContext db,
    MatchIngestService ingest,
    HistorySyncService history,
    TrackedPlayerService player,
    JobStatusService status,
    ILogger<ImportService> logger)
{
    public async Task ImportFolderAsync(string folder, CancellationToken ct)
    {
        try
        {
            var gamesDir = Directory.Exists(Path.Combine(folder, "games")) ? Path.Combine(folder, "games") : folder;
            var files = Directory.GetFiles(gamesDir, "*.json");
            var puuid = await ResolvePuuidAsync(files, ct);

            var imported = 0;
            var skipped = 0;
            var failed = 0;
            for (var i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await ImportGameFileAsync(files[i], puuid, ct)) imported++; else skipped++;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogWarning(ex, "Failed to import {File}", files[i]);
                }
                status.Report(i + 1, files.Length, $"{i + 1}/{files.Length} games ({imported} new, {skipped} already present)");
            }

            // The ledger lives next to the scripts (folder root or its parent).
            var ledgerImported = 0;
            foreach (var candidate in new[] { Path.Combine(folder, "lp-history.csv"), Path.Combine(Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder, "lp-history.csv") })
            {
                if (File.Exists(candidate))
                {
                    ledgerImported = await ImportLpLedgerAsync(candidate, ct);
                    break;
                }
            }

            var lpPerGame = Path.Combine(folder, "lp-per-game.csv");
            var lpApplied = File.Exists(lpPerGame) ? await ImportLpPerGameAsync(lpPerGame, ct) : 0;

            await history.AttributeLpFromLedgerAsync(ct);
            status.Finish($"done - {imported} games imported, {skipped} already present, {failed} failed, {ledgerImported} LP snapshots, {lpApplied} LP deltas applied");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import failed");
            status.Finish($"failed: {ex.Message}");
            throw;
        }
    }

    /// Prefers the API (needs a key); otherwise the tracked player is still
    /// identifiable offline - theirs is the one puuid present in every exported game.
    private async Task<string> ResolvePuuidAsync(string[] files, CancellationToken ct)
    {
        try
        {
            return await player.GetPuuidAsync(ct);
        }
        catch (Exception ex) when (ex is RiotApiKeyMissingException or RiotApiException)
        {
            HashSet<string>? common = null;
            foreach (var file in files.Take(10))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
                var puuids = doc.RootElement.GetProperty("match").GetProperty("metadata").GetProperty("participants")
                    .EnumerateArray().Select(p => p.GetString()!).ToHashSet();
                if (common is null) common = puuids; else common.IntersectWith(puuids);
                if (common.Count == 1) break;
            }
            if (common is not { Count: 1 })
            {
                throw new InvalidOperationException(
                    "Could not infer the tracked player from the export files (a duo partner in every game?). Configure a Riot API key and retry.", ex);
            }
            var inferred = common.Single();
            await player.StorePuuidAsync(inferred, ct);
            logger.LogInformation("No API key - inferred tracked player's puuid from export files.");
            return inferred;
        }
    }

    private async Task<bool> ImportGameFileAsync(string file, string puuid, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
        var matchId = doc.RootElement.GetProperty("matchId").GetString()!;
        if (await db.Matches.AnyAsync(m => m.Id == matchId, ct)) return false;

        var matchRaw = doc.RootElement.GetProperty("match").GetRawText();
        var timelineRaw = doc.RootElement.TryGetProperty("timeline", out var tl) && tl.ValueKind is not JsonValueKind.Null
            ? tl.GetRawText() : null;

        // Historic games: no rank lookups (they'd be today's ranks anyway) and the
        // raw file stays where it is - no point duplicating megabytes on disk.
        var match = await ingest.BuildMatchAsync(matchRaw, timelineRaw, puuid, withRanks: false, ranksAtGameTime: false, ct);
        match.RawPath = file;

        db.Matches.Add(match);
        if (await db.KnownMatches.FindAsync([matchId], ct) is null)
        {
            db.KnownMatches.Add(new KnownMatch { Id = matchId });
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<int> ImportLpLedgerAsync(string csvPath, CancellationToken ct)
    {
        var existing = (await db.LpSnapshots.Select(s => new { s.TimestampUtc, s.Queue }).ToListAsync(ct))
            .Select(s => (s.TimestampUtc, s.Queue)).ToHashSet();

        var added = 0;
        foreach (var row in ReadCsv(csvPath))
        {
            var timestamp = DateTime.Parse(row["Timestamp"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
            if (!existing.Add((timestamp, row["Queue"]))) continue;

            db.LpSnapshots.Add(new LpSnapshot
            {
                TimestampUtc = timestamp,
                Queue = row["Queue"],
                Tier = row["Tier"],
                Division = row["Division"],
                Lp = int.Parse(row["LP"]),
                Wins = int.Parse(row["Wins"]),
                Losses = int.Parse(row["Losses"]),
                RankValue = int.Parse(row["RankValue"]),
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return added;
    }

    private async Task<int> ImportLpPerGameAsync(string csvPath, CancellationToken ct)
    {
        var applied = 0;
        foreach (var row in ReadCsv(csvPath))
        {
            if (row["LPChange"] is not { Length: > 0 } change) continue;
            var match = await db.Matches.FindAsync([row["MatchId"]], ct);
            if (match is not { LpChange: null }) continue;

            match.LpChange = int.Parse(change);
            match.LpBefore = row.GetValueOrDefault("LPBefore");
            match.LpAfter = row.GetValueOrDefault("LPAfter");
            applied++;
        }
        await db.SaveChangesAsync(ct);
        return applied;
    }

    /// Minimal quoted-CSV reader for Export-Csv output (header row, all fields quoted).
    private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
    {
        using var reader = new StreamReader(path);
        if (reader.ReadLine() is not { } headerLine) yield break;
        var headers = ParseCsvLine(headerLine);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var fields = ParseCsvLine(line);
            var row = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count && i < fields.Count; i++) row[headers[i]] = fields[i];
            yield return row;
        }
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
