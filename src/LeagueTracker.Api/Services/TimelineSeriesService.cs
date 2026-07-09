using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

/// Per-player cumulative curves (gold / cs / damage to champions / xp) straight
/// from the raw timeline file - computed on demand, never persisted, so new
/// metrics cost a code change and nothing else.
public sealed class TimelineSeriesService(LeagueDbContext db)
{
    public async Task<object?> GetAsync(string matchId, CancellationToken ct)
    {
        var match = await db.Matches.AsNoTracking()
            .Include(m => m.Participants)
            .FirstOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is null || match.RawPath is not { Length: > 0 } || !File.Exists(match.RawPath)) return null;

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(match.RawPath, ct));
        if (!doc.RootElement.TryGetProperty("timeline", out var timeline)
            || timeline.ValueKind is JsonValueKind.Null
            || !timeline.TryGetProperty("info", out var info)
            || !info.TryGetProperty("frames", out var frames))
        {
            return null;
        }

        var byPid = match.Participants.OrderBy(p => p.ParticipantId).ToList();
        var minutes = new List<int>();
        var gold = byPid.ToDictionary(p => p.ParticipantId, _ => new List<int>());
        var cs = byPid.ToDictionary(p => p.ParticipantId, _ => new List<int>());
        var damage = byPid.ToDictionary(p => p.ParticipantId, _ => new List<int>());
        var xp = byPid.ToDictionary(p => p.ParticipantId, _ => new List<int>());

        foreach (var frame in frames.EnumerateArray())
        {
            if (!frame.TryGetProperty("participantFrames", out var pf)) continue;
            minutes.Add((int)Math.Round(frame.GetProperty("timestamp").GetInt64() / 60000.0));
            foreach (var p in byPid)
            {
                if (!pf.TryGetProperty(p.ParticipantId.ToString(), out var stats))
                {
                    gold[p.ParticipantId].Add(0); cs[p.ParticipantId].Add(0);
                    damage[p.ParticipantId].Add(0); xp[p.ParticipantId].Add(0);
                    continue;
                }
                gold[p.ParticipantId].Add(stats.TryGetProperty("totalGold", out var g) ? g.GetInt32() : 0);
                cs[p.ParticipantId].Add(
                    (stats.TryGetProperty("minionsKilled", out var mk) ? mk.GetInt32() : 0) +
                    (stats.TryGetProperty("jungleMinionsKilled", out var jk) ? jk.GetInt32() : 0));
                damage[p.ParticipantId].Add(
                    stats.TryGetProperty("damageStats", out var ds) && ds.TryGetProperty("totalDamageDoneToChampions", out var dmg)
                        ? dmg.GetInt32() : 0);
                xp[p.ParticipantId].Add(stats.TryGetProperty("xp", out var x) ? x.GetInt32() : 0);
            }
        }

        return new
        {
            Minutes = minutes,
            Players = byPid.Select(p => new
            {
                p.ParticipantId,
                p.Champion,
                p.IsMe,
                p.IsAlly,
                Gold = gold[p.ParticipantId],
                Cs = cs[p.ParticipantId],
                Damage = damage[p.ParticipantId],
                Xp = xp[p.ParticipantId],
            }),
        };
    }
}
