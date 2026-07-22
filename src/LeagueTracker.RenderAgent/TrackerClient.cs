using System.Net;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

public sealed record ClipEvent(string Kind, int TimeSec);

public sealed record ClipWindow(int Index, int StartSec, int EndSec, string Label, List<ClipEvent> Events);

public sealed record RenderJob(
    string Kind, string MatchId, string GameVersion, double DurationSec, string ReplayUrl,
    string? MyName, string? MyChampion, List<ClipWindow> Windows)
{
    public bool IsFullGame => Kind is "full";
}

/// The agent's half of the pull-based render queue on the tracker server.
public sealed class TrackerClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _agentName;

    public TrackerClient(string serverUrl, AgentConfig config)
    {
        ServerUrl = serverUrl;
        _agentName = config.AgentName;
        if (config is { CfAccessClientId.Length: > 0, CfAccessClientSecret.Length: > 0 })
        {
            _http.DefaultRequestHeaders.Add("CF-Access-Client-Id", config.CfAccessClientId);
            _http.DefaultRequestHeaders.Add("CF-Access-Client-Secret", config.CfAccessClientSecret);
        }
    }

    public string ServerUrl { get; }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ServerUrl}/api/status", ct);
            if (resp.IsSuccessStatusCode && !IsJson(resp))
            {
                // Cloudflare Access answers walled-off requests with a 200
                // sign-in page - that's the API being unreachable, not up.
                Log.Warn($"{ServerUrl} answered with a sign-in page, not the API - check the Access service token (CfAccessClientId/Secret)");
                return false;
            }
            return resp.IsSuccessStatusCode;
        }
        catch when (!ct.IsCancellationRequested)
        {
            return false;   // down/refusing server = unreachable, not a crash
        }
    }

    public async Task<RenderJob?> ClaimNextAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{ServerUrl}/api/render/next?agent={Uri.EscapeDataString(_agentName)}", null, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        if (!IsJson(resp))
        {
            throw new InvalidOperationException("got a sign-in page instead of a job - check the Access service token (CfAccessClientId/Secret)");
        }
        return JsonSerializer.Deserialize<RenderJob>(await resp.Content.ReadAsStringAsync(ct), Json);
    }

    /// Frees leases a previous incarnation of this agent took to its grave
    /// (crash, hard kill) so its interrupted jobs re-queue now instead of at
    /// lease expiry. Returns the released job keys. Best-effort: a tracker
    /// without the endpoint (not yet redeployed) just keeps expiry behavior.
    public async Task<List<string>> ReleaseStaleLeasesAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsync($"{ServerUrl}/api/render/release-stale?agent={Uri.EscapeDataString(_agentName)}", null, ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return [];
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return [.. doc.RootElement.GetProperty("released").EnumerateArray().Select(e => e.GetString()).OfType<string>()];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private static bool IsJson(HttpResponseMessage resp) =>
        resp.Content.Headers.ContentType?.MediaType is "application/json";

    public async Task DownloadReplayAsync(RenderJob job, string targetPath, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync($"{ServerUrl}{job.ReplayUrl}", ct);
        await using var file = File.Create(targetPath);
        await stream.CopyToAsync(file, ct);
    }

    public async Task UploadAsync(RenderJob job, int index, string mp4Path, CancellationToken ct)
    {
        var url = job.IsFullGame
            ? $"{ServerUrl}/api/render/{job.MatchId}/full"
            : $"{ServerUrl}/api/render/{job.MatchId}/clips/{index}";
        await using var file = File.OpenRead(mp4Path);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        using var resp = await _http.PutAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// Offers a recorded live-game VOD to this tracker. False when the
    /// tracker doesn't know the match (it belongs to another account's
    /// instance) - the caller tries the next tracker. The sidecar pieces
    /// only upload once the mp4 is accepted.
    public async Task<bool> UploadVodAsync(string matchId, string mp4Path, string? metaPath, string? eventsPath, string? thumbPath, CancellationToken ct)
    {
        await using (var file = File.OpenRead(mp4Path))
        {
            using var content = new StreamContent(file);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            using var resp = await _http.PutAsync($"{ServerUrl}/api/vods/{matchId}", content, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return false;
            resp.EnsureSuccessStatusCode();
        }
        foreach (var (name, path, type) in new[]
        {
            ("meta", metaPath, "application/json"),
            ("events", eventsPath, "application/gzip"),
            ("thumb", thumbPath, "image/jpeg"),
        })
        {
            if (path is null || !File.Exists(path)) continue;
            await using var side = File.OpenRead(path);
            using var content = new StreamContent(side);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            using var resp = await _http.PutAsync($"{ServerUrl}/api/vods/{matchId}/{name}", content, ct);
            resp.EnsureSuccessStatusCode();
        }
        return true;
    }

    public async Task CompleteAsync(RenderJob job, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{ServerUrl}/api/render/{job.MatchId}/complete?kind={job.Kind}", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task FailAsync(RenderJob job, string error, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsync($"{ServerUrl}/api/render/{job.MatchId}/fail?kind={job.Kind}", new StringContent(error), ct);
        }
        catch
        {
            // Reporting the failure failed too; the lease will expire on its own.
        }
    }
}
