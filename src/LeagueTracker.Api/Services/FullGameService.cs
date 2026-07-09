using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

public sealed record FullGameStatus(string State, bool Keep, double? SizeMb, DateTime? RenderedUtc, string? Error);

/// Full-game renders: opt-in per match (they cost ~500MB and a real-time render
/// on the gaming PC, so nothing here is automatic). Files-as-truth like the
/// clips: {matchId}.requested queues it, {matchId}.mp4 is the result,
/// {matchId}.keep exempts it from retention, {matchId}.failed.json blocks retries.
public sealed class FullGameService(LeagueDbContext db, ReplayArchiveService replays, DataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private string Root => Path.Combine(paths.DataDir, "fullgames");

    private static bool ValidId(string matchId) => matchId.All(ch => char.IsLetterOrDigit(ch) || ch is '_');

    private string Mp4(string matchId) => Path.Combine(Root, $"{matchId}.mp4");
    private string RequestMarker(string matchId) => Path.Combine(Root, $"{matchId}.requested");
    private string KeepMarker(string matchId) => Path.Combine(Root, $"{matchId}.keep");
    private string FailMarker(string matchId) => Path.Combine(Root, $"{matchId}.failed.json");

    public string? VideoPath(string matchId) =>
        ValidId(matchId) && File.Exists(Mp4(matchId)) ? Mp4(matchId) : null;

    public string VideoTargetPath(string matchId) => Mp4(matchId);

    public FullGameStatus Status(string matchId, RenderLeaseService leases)
    {
        if (!ValidId(matchId)) return new("none", false, null, null, null);
        if (VideoPath(matchId) is { } path)
        {
            var info = new FileInfo(path);
            return new("done", File.Exists(KeepMarker(matchId)), Math.Round(info.Length / 1024.0 / 1024.0, 1), info.LastWriteTimeUtc, null);
        }
        if (FailReason(matchId) is { } error) return new("failed", false, null, null, error);
        if (leases.IsLeased($"full:{matchId}")) return new("rendering", false, null, null, null);
        if (File.Exists(RequestMarker(matchId))) return new("requested", false, null, null, null);
        return new("none", false, null, null, null);
    }

    /// Queues a render; only meaningful while the .rofl is still archived.
    public string? Request(string matchId)
    {
        if (!ValidId(matchId)) return "invalid match id";
        if (replays.PathFor(matchId) is null) return "no archived replay for this game";
        if (File.Exists(Mp4(matchId))) return null;   // already rendered - nothing to do
        Directory.CreateDirectory(Root);
        File.Delete(FailMarker(matchId));
        if (!File.Exists(RequestMarker(matchId))) File.WriteAllText(RequestMarker(matchId), DateTime.UtcNow.ToString("o"));
        return null;
    }

    public void CompleteRequest(string matchId)
    {
        if (!ValidId(matchId)) return;
        File.Delete(RequestMarker(matchId));
        File.Delete(FailMarker(matchId));
    }

    public void Delete(string matchId)
    {
        if (!ValidId(matchId)) return;
        File.Delete(Mp4(matchId));
        File.Delete(KeepMarker(matchId));
        File.Delete(RequestMarker(matchId));
        File.Delete(FailMarker(matchId));
    }

    public bool ToggleKeep(string matchId)
    {
        if (!ValidId(matchId) || !File.Exists(Mp4(matchId))) return false;
        var marker = KeepMarker(matchId);
        if (File.Exists(marker)) { File.Delete(marker); return false; }
        File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        return true;
    }

    public string? FailReason(string matchId)
    {
        if (!ValidId(matchId) || !File.Exists(FailMarker(matchId))) return null;
        try
        {
            return JsonDocument.Parse(File.ReadAllText(FailMarker(matchId))).RootElement.GetProperty("error").GetString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    public void MarkFailed(string matchId, string error)
    {
        if (!ValidId(matchId)) return;
        Directory.CreateDirectory(Root);
        File.Delete(RequestMarker(matchId));
        File.WriteAllText(FailMarker(matchId), JsonSerializer.Serialize(new { error, atUtc = DateTime.UtcNow }, Json));
    }

    /// Pending full-game requests, newest game first, for the render queue.
    public List<string> PendingRequests()
    {
        if (!Directory.Exists(Root)) return [];
        return [.. Directory.EnumerateFiles(Root, "*.requested")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(id => !File.Exists(Mp4(id)))];
    }

    public async Task<List<object>> QueueRowsAsync(RenderLeaseService leases, CancellationToken ct)
    {
        var ids = PendingRequests()
            .Concat(Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "*.failed.json").Select(f => Path.GetFileName(f)!.Replace(".failed.json", "")) : [])
            .Distinct().ToList();
        if (ids is not { Count: > 0 }) return [];

        var matches = await db.Matches.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new { m.Id, m.Champion, m.GameEndUtc })
            .ToListAsync(ct);
        return [.. matches.OrderByDescending(m => m.GameEndUtc).Select(m => (object)new
        {
            MatchId = m.Id,
            m.Champion,
            m.GameEndUtc,
            Kind = "full",
            Status = FailReason(m.Id) is not null ? "failed" : leases.IsLeased($"full:{m.Id}") ? "rendering" : "pending",
            Error = FailReason(m.Id),
        })];
    }

    /// Deletes unkept renders older than the retention window; returns how many.
    public int SweepRetention(int retentionDays)
    {
        if (!Directory.Exists(Root) || retentionDays <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = 0;
        foreach (var mp4 in Directory.EnumerateFiles(Root, "*.mp4"))
        {
            var matchId = Path.GetFileNameWithoutExtension(mp4);
            if (File.Exists(KeepMarker(matchId))) continue;
            if (File.GetLastWriteTimeUtc(mp4) >= cutoff) continue;
            File.Delete(mp4);
            deleted++;
        }
        return deleted;
    }
}
