using System.Text.Json;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// The player's challenge standings against the whole ladder (Challenges-V1) -
/// the one benchmark our own wins-vs-losses analysis can't provide. Player data
/// is refreshed on a TTL; the challenge config (id -> name) is near-static and
/// cached long. Both are snapshotted in the KeyValue table.
public sealed class ChallengesBenchmarkService(
    LeagueDbContext db, RiotApiClient riot, TrackedPlayerService player, ILogger<ChallengesBenchmarkService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] LevelOrder =
        ["NONE", "IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "DIAMOND", "MASTER", "GRANDMASTER", "CHALLENGER"];

    public async Task<object?> GetAsync(CancellationToken ct)
    {
        var puuid = await player.GetPuuidAsync(ct);
        var playerRaw = await CachedAsync("challenges:playerdata", TimeSpan.FromHours(12),
            () => riot.GetChallengesPlayerDataRawAsync(puuid, ct), ct);
        var configRaw = await CachedAsync("challenges:config", TimeSpan.FromDays(7),
            () => riot.GetChallengesConfigRawAsync(ct), ct);
        if (playerRaw is null || configRaw is null) return null;

        var names = ParseConfigNames(configRaw);
        var rows = new List<object>();
        using var doc = JsonDocument.Parse(playerRaw);
        if (!doc.RootElement.TryGetProperty("challenges", out var challenges)) return null;

        foreach (var c in challenges.EnumerateArray())
        {
            var id = c.GetProperty("challengeId").GetInt64();
            var level = c.TryGetProperty("level", out var lv) ? lv.GetString() ?? "NONE" : "NONE";
            if (level is "NONE") continue;
            if (!names.TryGetValue(id, out var meta)) continue;   // capstone/category roots have no leaf name

            rows.Add(new
            {
                Id = id,
                meta.Name,
                meta.Description,
                Level = level,
                LevelRank = Array.IndexOf(LevelOrder, level),
                Percentile = c.TryGetProperty("percentile", out var pc) ? Math.Round(pc.GetDouble(), 4) : (double?)null,
                Value = c.TryGetProperty("value", out var vv) ? vv.GetDouble() : (double?)null,
            });
        }

        return new
        {
            AsOfUtc = (await db.KeyValues.FindAsync(["challenges:playerdata"], ct))?.Value is { } j
                && JsonDocument.Parse(j).RootElement.TryGetProperty("fetchedAt", out var f) ? f.GetString() : null,
            Challenges = rows,
        };
    }

    private async Task<string?> CachedAsync(string key, TimeSpan ttl, Func<Task<string>> fetch, CancellationToken ct)
    {
        var existing = await db.KeyValues.FindAsync([key], ct);
        if (existing is not null)
        {
            try
            {
                var wrap = JsonDocument.Parse(existing.Value).RootElement;
                var fetchedAt = DateTime.Parse(wrap.GetProperty("fetchedAt").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (DateTime.UtcNow - fetchedAt < ttl) return wrap.GetProperty("data").GetRawText();
            }
            catch { /* corrupt cache - refetch */ }
        }

        try
        {
            var data = await fetch();
            var wrapped = $"{{\"fetchedAt\":\"{DateTime.UtcNow:o}\",\"data\":{data}}}";
            if (existing is null) db.KeyValues.Add(new KeyValue { Key = key, Value = wrapped });
            else existing.Value = wrapped;
            await db.SaveChangesAsync(ct);
            return data;
        }
        catch (RiotApiException ex)
        {
            logger.LogWarning("Challenges fetch failed ({Status}); serving stale if available", ex.StatusCode);
            // Fall back to whatever is cached, even if stale.
            if (existing is not null)
            {
                try { return JsonDocument.Parse(existing.Value).RootElement.GetProperty("data").GetRawText(); }
                catch { return null; }
            }
            return null;
        }
    }

    private static Dictionary<long, (string Name, string Description)> ParseConfigNames(string configRaw)
    {
        var map = new Dictionary<long, (string, string)>();
        using var doc = JsonDocument.Parse(configRaw);
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (!c.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetInt64();
            if (!c.TryGetProperty("localizedNames", out var loc) || !loc.TryGetProperty("en_US", out var en)) continue;
            var name = en.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var desc = en.TryGetProperty("shortDescription", out var sd) ? sd.GetString() ?? ""
                : en.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            if (name is { Length: > 0 }) map[id] = (name, desc);
        }
        return map;
    }
}
