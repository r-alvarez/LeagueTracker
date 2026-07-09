using System.Net;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

public sealed record ClipEvent(string Kind, int TimeSec);

public sealed record ClipWindow(int Index, int StartSec, int EndSec, string Label, List<ClipEvent> Events);

public sealed record RenderJob(
    string MatchId, string GameVersion, double DurationSec, string ReplayUrl,
    string? MyName, string? MyChampion, List<ClipWindow> Windows);

/// The agent's half of the pull-based render queue on the tracker server.
public sealed class TrackerClient(string serverUrl, string agentName)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{serverUrl}/api/status", ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> PlayerInGameAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{serverUrl}/api/live", ct);
        return resp.StatusCode == HttpStatusCode.OK;
    }

    public async Task<RenderJob?> ClaimNextAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{serverUrl}/api/render/next?agent={Uri.EscapeDataString(agentName)}", null, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<RenderJob>(await resp.Content.ReadAsStringAsync(ct), Json);
    }

    public async Task DownloadReplayAsync(RenderJob job, string targetPath, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync($"{serverUrl}{job.ReplayUrl}", ct);
        await using var file = File.Create(targetPath);
        await stream.CopyToAsync(file, ct);
    }

    public async Task UploadClipAsync(string matchId, int index, string mp4Path, CancellationToken ct)
    {
        await using var file = File.OpenRead(mp4Path);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        using var resp = await _http.PutAsync($"{serverUrl}/api/render/{matchId}/clips/{index}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CompleteAsync(string matchId, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{serverUrl}/api/render/{matchId}/complete", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task FailAsync(string matchId, string error, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsync($"{serverUrl}/api/render/{matchId}/fail", new StringContent(error), ct);
        }
        catch
        {
            // Reporting the failure failed too; the lease will expire on its own.
        }
    }
}
