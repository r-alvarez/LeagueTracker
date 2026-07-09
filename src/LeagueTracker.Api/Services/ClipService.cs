using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

public sealed record ClipEvent(string Kind, int TimeSec);

public sealed record ClipWindow(int Index, int StartSec, int EndSec, string Label, List<ClipEvent> Events);

public sealed record ClipPlan(string MatchId, string GameVersion, double DurationSec, List<ClipWindow> Windows);

/// Plans and stores the per-match highlight clips that the render agent turns
/// into mp4s. Follows the app's files-as-truth rule: the plan manifest and the
/// rendered clips live under data/clips/{matchId}; the db is never written.
public sealed class ClipService(LeagueDbContext db, ReplayArchiveService replays, DataPaths paths)
{
    // A fight window is [event - pre, event + post]; overlapping windows merge,
    // so a kill followed by your death 15s later reviews as one clip.
    private const int PreRollSec = 20;
    private const int PostRollSec = 10;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private string ClipsRoot => Path.Combine(paths.DataDir, "clips");

    private string? DirFor(string matchId) =>
        matchId.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_') ? null : Path.Combine(ClipsRoot, matchId);

    /// Kill/death moments merged into fight windows. Null when the match is
    /// unknown or has no timeline-derived kill events to plan from.
    public async Task<ClipPlan?> PlanAsync(string matchId, CancellationToken ct)
    {
        if (DirFor(matchId) is null) return null;
        var match = await db.Matches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is not { HasTimeline: true }) return null;

        var myPid = await db.Participants.AsNoTracking()
            .Where(p => p.MatchId == matchId && p.IsMe)
            .Select(p => (int?)p.ParticipantId)
            .FirstOrDefaultAsync(ct);
        if (myPid is null) return null;

        var events = await db.KillEvents.AsNoTracking()
            .Where(k => k.MatchId == matchId && (k.KillerParticipantId == myPid || k.VictimParticipantId == myPid))
            .OrderBy(k => k.TimeSec)
            .Select(k => new ClipEvent(k.VictimParticipantId == myPid ? "death" : "kill", k.TimeSec))
            .ToListAsync(ct);
        if (events is not { Count: > 0 }) return null;

        var windows = new List<ClipWindow>();
        var group = new List<ClipEvent> { events[0] };
        foreach (var e in events.Skip(1))
        {
            if (e.TimeSec - PreRollSec <= group[^1].TimeSec + PostRollSec)
            {
                group.Add(e);
            }
            else
            {
                windows.Add(ToWindow(windows.Count, group, match.DurationSec));
                group = [e];
            }
        }
        windows.Add(ToWindow(windows.Count, group, match.DurationSec));

        return new ClipPlan(matchId, match.GameVersion, match.DurationSec, windows);
    }

    private static ClipWindow ToWindow(int index, List<ClipEvent> events, double durationSec)
    {
        var kills = events.Count(e => e.Kind is "kill");
        var deaths = events.Count(e => e.Kind is "death");
        var label = (kills, deaths) switch
        {
            (> 0, > 0) => "fight",
            (1, _) => "kill",
            (> 1, _) => $"{kills}-kills",
            _ => "death",
        };
        return new ClipWindow(
            index,
            Math.Max(0, events[0].TimeSec - PreRollSec),
            (int)Math.Min(durationSec, events[^1].TimeSec + PostRollSec),
            label,
            events);
    }

    /// Persist the plan next to where the clips will land, so the clip list
    /// survives db rebuilds and the agent's uploads can be validated against it.
    public async Task SavePlanAsync(ClipPlan plan, CancellationToken ct)
    {
        var dir = DirFor(plan.MatchId)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.json"), JsonSerializer.Serialize(plan, Json), ct);
    }

    public async Task<ClipPlan?> LoadPlanAsync(string matchId, CancellationToken ct)
    {
        if (DirFor(matchId) is not { } dir) return null;
        var path = Path.Combine(dir, "plan.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ClipPlan>(await File.ReadAllTextAsync(path, ct), Json);
        }
        catch
        {
            return null;
        }
    }

    public string? ClipPath(string matchId, int index)
    {
        if (DirFor(matchId) is not { } dir) return null;
        var path = Path.Combine(dir, $"w{index:00}.mp4");
        return File.Exists(path) ? path : null;
    }

    public string ClipTargetPath(string matchId, int index) => Path.Combine(DirFor(matchId)!, $"w{index:00}.mp4");

    public bool HasClips(string matchId) =>
        DirFor(matchId) is { } dir && Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.mp4").Any();

    public string? FailReason(string matchId)
    {
        if (DirFor(matchId) is not { } dir) return null;
        var marker = Path.Combine(dir, "render-failed.json");
        if (!File.Exists(marker)) return null;
        try
        {
            return JsonDocument.Parse(File.ReadAllText(marker)).RootElement.GetProperty("error").GetString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    public async Task MarkFailedAsync(string matchId, string error, CancellationToken ct)
    {
        if (DirFor(matchId) is not { } dir) return;
        Directory.CreateDirectory(dir);
        var marker = JsonSerializer.Serialize(new { error, atUtc = DateTime.UtcNow }, Json);
        await File.WriteAllTextAsync(Path.Combine(dir, "render-failed.json"), marker, ct);
    }

    public void ClearFailed(string matchId)
    {
        if (DirFor(matchId) is not { } dir) return;
        var marker = Path.Combine(dir, "render-failed.json");
        if (File.Exists(marker)) File.Delete(marker);
    }

    /// Render-queue view over every match with an archived replay, newest first.
    public async Task<List<object>> QueueAsync(RenderLeaseService leases, CancellationToken ct)
    {
        var archived = replays.ArchivedMatchIds();
        var matches = await db.Matches.AsNoTracking()
            .Where(m => archived.Contains(m.Id))
            .OrderByDescending(m => m.GameEndUtc)
            .Select(m => new { m.Id, m.Champion, m.GameEndUtc, m.HasTimeline })
            .ToListAsync(ct);

        var rows = new List<object>();
        foreach (var m in matches)
        {
            var clips = HasClips(m.Id);
            var failed = FailReason(m.Id);
            var status = clips ? "done"
                : failed is not null ? "failed"
                : leases.IsLeased($"clips:{m.Id}") ? "rendering"
                : m.HasTimeline && await PlanAsync(m.Id, ct) is { Windows.Count: > 0 } ? "pending"
                : "no-events";
            rows.Add(new { MatchId = m.Id, m.Champion, m.GameEndUtc, Kind = "clips", Status = status, Error = failed });
        }
        return rows;
    }
}
