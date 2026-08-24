using System.Text.Json;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Services;

public sealed record ClipEvent(string Kind, int TimeSec);

/// Kind "moment" = the player's own kills/deaths (camera on them); "fight" =
/// a team fight they were not part of, filmed from CameraName's POV so the
/// replay shows footage the player's own screen never had. Defaults keep
/// pre-existing plan manifests deserializable.
public sealed record ClipWindow(int Index, int StartSec, int EndSec, string Label, List<ClipEvent> Events,
    string Kind = "moment", string? CameraName = null, string? CameraChampion = null);

public sealed record ClipPlan(string MatchId, string GameVersion, double DurationSec, List<ClipWindow> Windows);

/// Plans and stores the per-match highlight clips that the render agent turns
/// into mp4s. Follows the app's files-as-truth rule: the plan manifest and the
/// rendered clips live under data/clips/{matchId}; the db is never written.
public sealed class ClipService(LeagueDbContext db, ReplayArchiveService replays, VodService vods, DataPaths paths)
{
    // A fight window is [first event - pre, chained end + post]; overlapping
    // windows merge, so a kill followed by your death 15s later reviews as one
    // clip. The chained end follows the whole play, not just the player's own
    // last event - teammates finishing the fight, the chase-down after - by
    // chaining ANY kills that stay close in time and space (the same
    // clustering TimelineAnalyzer uses for fights).
    private const int PreRollSec = 20;
    private const int PostRollSec = 10;
    private const int ChainSec = 15;
    private const int ChainUnits = 3500;

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

        // Every kill in the match, not just the player's: the chained window
        // end needs the kills around their events too.
        var kills = await db.KillEvents.AsNoTracking()
            .Where(k => k.MatchId == matchId)
            .OrderBy(k => k.TimeSec)
            .Select(k => new Kill(k.TimeSec, k.KillerParticipantId, k.VictimParticipantId, k.X, k.Y))
            .ToListAsync(ct);
        var mine = kills.Where(k => k.KillerId == myPid || k.VictimId == myPid).ToList();

        var windows = new List<ClipWindow>();
        if (mine.Count > 0)
        {
            var group = new List<Kill> { mine[0] };
            var groupEnd = ChainedEndSec(mine[0], kills);
            foreach (var k in mine.Skip(1))
            {
                if (k.TimeSec - PreRollSec <= groupEnd + PostRollSec)
                {
                    group.Add(k);
                    groupEnd = Math.Max(groupEnd, ChainedEndSec(k, kills));
                }
                else
                {
                    windows.Add(ToWindow(windows.Count, group, myPid.Value, groupEnd, match.DurationSec));
                    group = [k];
                    groupEnd = ChainedEndSec(k, kills);
                }
            }
            windows.Add(ToWindow(windows.Count, group, myPid.Value, groupEnd, match.DurationSec));
        }
        windows.AddRange(await FightWindowsAsync(match, myPid.Value, windows.Count, ct));

        return windows is { Count: > 0 } ? new ClipPlan(matchId, match.GameVersion, match.DurationSec, windows) : null;
    }

    /// The team's skirmishes/teamfights the player was NOT in, filmed from a
    /// surviving fighter's POV (the analyzer picks who). The player's own
    /// screen never showed these - a replay clip is the only footage of them.
    /// Duels elsewhere stay unclipped: solo trades are noise at review time.
    private async Task<List<ClipWindow>> FightWindowsAsync(Data.Match match, int myPid, int nextIndex, CancellationToken ct)
    {
        if (match.FightsJson is not { Length: > 0 }) return [];
        List<TimelineAnalyzer.Fight>? fights;
        try
        {
            fights = JsonSerializer.Deserialize<List<TimelineAnalyzer.Fight>>(match.FightsJson, Json);
        }
        catch
        {
            return [];
        }
        // Significance gate: teamfights always; skirmishes only when 2+ kills
        // changed hands - a lone jungle gank elsewhere is a marker, not a clip.
        var wanted = (fights ?? [])
            .Where(f => !f.Participated && f.CameraParticipantId > 0
                && (f.Kind is "teamfight" || (f.Kind is "skirmish" && f.AllyKills + f.EnemyKills >= 2)))
            .ToList();
        if (wanted is not { Count: > 0 }) return [];

        var fighters = await db.Participants.AsNoTracking()
            .Where(p => p.MatchId == match.Id)
            .Select(p => new { p.ParticipantId, p.RiotId, p.Champion })
            .ToListAsync(ct);

        var windows = new List<ClipWindow>();
        foreach (var f in wanted)
        {
            var camera = fighters.FirstOrDefault(p => p.ParticipantId == f.CameraParticipantId);
            if (camera is null) continue;
            windows.Add(new ClipWindow(
                nextIndex + windows.Count,
                Math.Max(0, f.StartSec - PreRollSec),
                (int)Math.Min(match.DurationSec, f.EndSec + PostRollSec),
                $"{f.Kind} {f.Allies}v{f.Enemies} · {f.Result}",
                [new ClipEvent("fight", f.StartSec)],
                Kind: "fight",
                CameraName: camera.RiotId is { Length: > 0 } riotId ? riotId.Split('#')[0] : null,
                CameraChampion: camera.Champion));
        }
        return windows;
    }

    private sealed record Kill(int TimeSec, int KillerId, int VictimId, int X, int Y);

    /// Where the play actually ends: from the given kill, follow ANY kills
    /// that land within ChainSec/ChainUnits of the last chained one. A kill
    /// elsewhere on the map is skipped, not a chain-breaker.
    private static int ChainedEndSec(Kill from, List<Kill> kills)
    {
        var (t, x, y) = (from.TimeSec, from.X, from.Y);
        foreach (var k in kills)
        {
            if (k.TimeSec <= t) continue;
            if (k.TimeSec - t > ChainSec) break;
            if (Math.Sqrt(Math.Pow(k.X - x, 2) + Math.Pow(k.Y - y, 2)) > ChainUnits) continue;
            (t, x, y) = (k.TimeSec, k.X, k.Y);
        }
        return t;
    }

    private static ClipWindow ToWindow(int index, List<Kill> group, int myPid, int endEventSec, double durationSec)
    {
        var events = group.Select(k => new ClipEvent(k.VictimId == myPid ? "death" : "kill", k.TimeSec)).ToList();
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
            Math.Max(0, group[0].TimeSec - PreRollSec),
            (int)Math.Min(durationSec, endEventSec + PostRollSec),
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
        var dismissed = Path.Combine(dir, "render-dismissed");
        if (File.Exists(dismissed)) File.Delete(dismissed);
    }

    /// Acknowledge a dead render (patch mismatch, replay sim-hang - things a
    /// retry can never fix): keep the fail marker so render/next still skips
    /// it, but hide it from the queue and its counts. Retry lifts it.
    public bool Dismiss(string matchId)
    {
        if (DirFor(matchId) is not { } dir || FailReason(matchId) is null) return false;
        File.WriteAllText(Path.Combine(dir, "render-dismissed"), DateTime.UtcNow.ToString("o"));
        return true;
    }

    public bool IsDismissed(string matchId) =>
        DirFor(matchId) is { } dir && File.Exists(Path.Combine(dir, "render-dismissed"));

    /// Windows the agent proved can never render (the replay simulation
    /// hangs at the spot, a camera target the replay doesn't know): kept out
    /// of render/next and the missing counts, so the match reads done with a
    /// named gap instead of failing forever. An owner retry lifts the verdict
    /// along with everything else.
    public async Task MarkUnrenderableAsync(string matchId, Dictionary<int, string> windows, CancellationToken ct)
    {
        if (DirFor(matchId) is not { } dir) return;
        Directory.CreateDirectory(dir);
        var merged = UnrenderableWindows(matchId);
        foreach (var (index, reason) in windows) merged[index] = reason;
        await File.WriteAllTextAsync(Path.Combine(dir, "unrenderable.json"), JsonSerializer.Serialize(merged, Json), ct);
    }

    public Dictionary<int, string> UnrenderableWindows(string matchId)
    {
        try
        {
            if (DirFor(matchId) is not { } dir) return [];
            var path = Path.Combine(dir, "unrenderable.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    public void ClearUnrenderable(string matchId)
    {
        if (DirFor(matchId) is not { } dir) return;
        var path = Path.Combine(dir, "unrenderable.json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// Drops rendered clips so the match re-qualifies for the render queue,
    /// and the plan with them: nothing is pinned to the old window indices any
    /// more, so the re-render should use current analysis (camera targets in
    /// particular) rather than replaying whatever was planned at claim time.
    public void DeleteClips(string matchId)
    {
        if (DirFor(matchId) is not { } dir || !Directory.Exists(dir)) return;
        foreach (var mp4 in Directory.EnumerateFiles(dir, "*.mp4")) File.Delete(mp4);
        DeletePlan(matchId);
    }

    /// Drops one bad clip so just that window re-renders. Also clears the
    /// failed marker - a match can hold good clips AND a failure (e.g. the
    /// game died mid-job), and the marker would otherwise block the re-render.
    /// The plan survives while any clip does: the surviving mp4s are named by
    /// its window indices. Once the last one goes, it is free to be replanned.
    public bool DeleteClip(string matchId, int index)
    {
        if (ClipPath(matchId, index) is not { } path) return false;
        File.Delete(path);
        ClearFailed(matchId);
        if (!HasClips(matchId)) DeletePlan(matchId);
        return true;
    }

    private void DeletePlan(string matchId)
    {
        if (DirFor(matchId) is not { } dir) return;
        var plan = Path.Combine(dir, "plan.json");
        if (File.Exists(plan)) File.Delete(plan);
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
            var failed = FailReason(m.Id);
            if (failed is not null && IsDismissed(m.Id)) continue; // dealt with - off the board
            // The saved plan is the manifest existing clips were rendered
            // against; only never-claimed matches need a fresh plan.
            var plan = await LoadPlanAsync(m.Id, ct) ?? (m.HasTimeline ? await PlanAsync(m.Id, ct) : null);
            var unrenderable = UnrenderableWindows(m.Id);
            string status;
            if (plan is not { Windows.Count: > 0 })
            {
                status = HasClips(m.Id) ? "done" : failed is not null ? "failed" : "no-events";
            }
            else
            {
                // A VOD-covered match only ever renders its "fight" windows
                // (the render/next rule) - counting the others against it
                // reported every reviewed match as "partial" forever. Windows
                // the agent proved unrenderable don't count against it either:
                // the gap is named in its own column, not held as "partial".
                var vodCovered = vods.HasVod(m.Id) || vods.ReadLink(m.Id) is not null;
                var renderable = plan.Windows
                    .Where(w => (!vodCovered || w.Kind is "fight") && !unrenderable.ContainsKey(w.Index)).ToList();
                var missing = renderable.Count(w => ClipPath(m.Id, w.Index) is null);
                status = missing == 0 ? "done"
                    : failed is not null ? "failed"
                    : leases.IsLeased($"clips:{m.Id}") ? "rendering"
                    : missing < renderable.Count ? "partial"
                    : "pending";
            }
            var gaps = unrenderable is { Count: > 0 }
                ? $"window(s) {string.Join(", ", unrenderable.Keys.OrderBy(k => k))} unrenderable - {string.Join("; ", unrenderable.Values.Distinct())}"
                : null;
            rows.Add(new { MatchId = m.Id, m.Champion, m.GameEndUtc, Kind = "clips", Status = status, Error = failed, Gaps = gaps });
        }
        return rows;
    }
}
