using System.Text.Json;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// Back-fills the LP-over-time history from dpm.lol's daily rank histogram for
/// the days before this tracker existed. Their widget serves one closing
/// tier/division/LP row per active day (solo queue), reaching months further
/// back than our own snapshot ledger.
///
/// Deliberate limits:
///  - Only days strictly BEFORE our earliest real snapshot are imported - the
///    live-captured region stays exactly as the poller wrote it.
///  - Imported rows carry Wins=0/Losses=0 because the histogram has no
///    cumulative counters. Per-game LP attribution requires the win+loss
///    counter to move by exactly one across a bracket, so zeroed rows can
///    never be used to attribute (or mis-attribute) a game's LP - they only
///    extend the chart.
///  - dpm.lol is an unofficial source with no per-game LP anywhere in its API
///    (verified: the match-history lp field is null account-wide), so this is
///    a daily-resolution back-fill by design, not a limitation to fix here.
public sealed class DpmLpBackfillService(
    HttpClient http, LeagueDbContext db, LpService lp, TrackedPlayerService player, ILogger<DpmLpBackfillService> logger)
{
    private const string SoloQueue = "Solo/Duo";

    public async Task<object> BackfillAsync(CancellationToken ct)
    {
        var puuid = await player.GetPuuidAsync(ct);
        var raw = await http.GetStringAsync($"https://dpm.lol/v1/players/{puuid}/widgets/rank-history", ct);

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("histogram", out var histogram))
            return new { Imported = 0, Message = "dpm.lol returned no rank histogram for this account." };

        // Everything at/after our first real snapshot belongs to the poller.
        var earliestLocal = await db.LpSnapshots
            .Where(s => s.Queue == SoloQueue)
            .OrderBy(s => s.TimestampUtc)
            .Select(s => (DateTime?)s.TimestampUtc)
            .FirstOrDefaultAsync(ct);
        var cutoffDate = earliestLocal?.Date ?? DateTime.UtcNow.Date.AddDays(1);

        var existingBackfilled = (await db.LpSnapshots
                .Where(s => s.Queue == SoloQueue && s.TimestampUtc < cutoffDate)
                .Select(s => s.TimestampUtc)
                .ToListAsync(ct))
            .ToHashSet();

        var rows = new List<LpSnapshot>();
        foreach (var day in histogram.EnumerateArray())
        {
            if (!DateTime.TryParse(day.GetProperty("date").GetString(), null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var date))
                continue;
            if (date.Date >= cutoffDate) continue;

            var tier = day.GetProperty("tier").GetString() ?? "";
            var division = day.GetProperty("rank").GetString() ?? "";
            var points = day.GetProperty("leaguePoints").GetInt32();
            if (RankMath.ToValue(tier, division, points) is not { } rankValue) continue;

            // The histogram row is the day's closing rank - stamp end of day.
            var timestamp = DateTime.SpecifyKind(date.Date.AddHours(23).AddMinutes(59), DateTimeKind.Utc);
            if (existingBackfilled.Contains(timestamp)) continue;

            rows.Add(new LpSnapshot
            {
                TimestampUtc = timestamp,
                Queue = SoloQueue,
                Tier = tier,
                Division = division,
                Lp = points,
                Wins = 0,
                Losses = 0,
                RankValue = rankValue,
            });
        }

        if (rows is { Count: > 0 })
        {
            db.LpSnapshots.AddRange(rows);
            await db.SaveChangesAsync(ct);
            lp.MirrorToCsv(rows);
        }

        logger.LogInformation("dpm.lol LP backfill: {Count} daily snapshots imported (before {Cutoff:yyyy-MM-dd})", rows.Count, cutoffDate);
        return new
        {
            Imported = rows.Count,
            From = rows.Count > 0 ? rows.Min(r => r.TimestampUtc).ToString("yyyy-MM-dd") : null,
            To = rows.Count > 0 ? rows.Max(r => r.TimestampUtc).ToString("yyyy-MM-dd") : null,
            Message = rows.Count > 0
                ? $"{rows.Count} daily LP snapshots imported from dpm.lol."
                : "Nothing new to import - all dpm.lol days are already covered.",
        };
    }
}
