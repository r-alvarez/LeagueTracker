using System.Net;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

public sealed record ClipEvent(string Kind, int TimeSec);

/// One stop on the between-games review: a replay window in game-clock
/// seconds, what it is, and the facts to check it against.
public sealed record ReviewMoment(
    string Kind, int TimeSec, int StartSec, int EndSec, string Title, string Detail);

public sealed record ReviewReel(
    string MatchId, string? MyRiotId, string? MyChampion, List<ReviewMoment> Moments);

/// CameraName/CameraChampion override the job-level follow target for this
/// window - "fight" windows film a team fight the player was not in from a
/// surviving fighter's POV. Null = follow the job's player as always.
public sealed record ClipWindow(int Index, int StartSec, int EndSec, string Label, List<ClipEvent> Events,
    string Kind = "moment", string? CameraName = null, string? CameraChampion = null);

public sealed record AgentRelease(string Version, string File, long SizeBytes, string Sha256);

/// The tracker's answer to a heartbeat: the newest published version, and a
/// one-shot command the owner queued from the Data page (with a token so the
/// agent runs it once and a restart never re-triggers it).
public sealed record HeartbeatReply(string? Latest, string? Command, string? CommandToken);

public sealed record RenderJob(
    string Kind, string MatchId, string GameVersion, double DurationSec, string ReplayUrl,
    string? MyName, string? MyChampion, List<ClipWindow> Windows)
{
    public bool IsFullGame => Kind is "full";
}

/// Path is the server's canonical address ("euw/ImRA-87166"); a server from
/// before regions only gives the slug, which still resolves.
public sealed record TrackerAccount(string Slug, string Label, string RiotId, string? Path = null)
{
    public string UrlPath => Path is { Length: > 0 } ? string.Join('/', Path.Split('/').Select(Uri.EscapeDataString)) : Uri.EscapeDataString(Slug);
}

/// The agent's half of the pull-based render queue on the tracker server.
/// One instance per tracked ACCOUNT: on a one-site server that is
/// {Api}/a/{RiotId}, on a legacy single-account server it is
/// {ServerUrl}/api - either way "does this tracker know the match" (404)
/// is the ownership probe, exactly as it was with one host per account.
public sealed class TrackerClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _agentName;

    private TrackerClient(string serverUrl, string api, TrackerAccount? account, AgentConfig config, bool keyed)
    {
        ServerUrl = serverUrl;
        Api = api;
        Account = account;
        Keyed = keyed;
        _agentName = config.AgentName;
        if (config is { CfAccessClientId.Length: > 0, CfAccessClientSecret.Length: > 0 })
        {
            _http.DefaultRequestHeaders.Add("CF-Access-Client-Id", config.CfAccessClientId);
            _http.DefaultRequestHeaders.Add("CF-Access-Client-Secret", config.CfAccessClientSecret);
        }
        // Always presented; the server ignores it on Access-authenticated
        // routes and requires it on the agent slice.
        _http.DefaultRequestHeaders.Add("X-Agent-Key", AgentKey.Load());
    }

    /// The server itself (agent endpoints, discovery, enrolment) - also the
    /// tracker for a legacy single-account server.
    public static TrackerClient ForServer(string serverUrl, AgentConfig config) =>
        new(serverUrl, $"{serverUrl}/api", null, config, keyed: false);

    /// One account on a one-site server. Keyed = this machine is approved
    /// there, so account calls go through the agent slice (/api/agent/a/...,
    /// which Access lets through on the key); otherwise the human routes
    /// behind an Access service token, as before.
    public static TrackerClient ForAccount(string serverUrl, TrackerAccount account, AgentConfig config, bool keyed) =>
        new(serverUrl, keyed ? $"{serverUrl}/api/agent/a/{account.UrlPath}" : $"{serverUrl}/api/a/{account.UrlPath}", account, config, keyed);

    /// Whether this machine may talk to the server on its key alone.
    public bool Keyed { get; private set; }
    /// The server client learns it after enrolment says "approved".
    public void MarkKeyed() => Keyed = true;

    /// Enrol (or re-announce) this machine and learn where it stands:
    /// "approved", "pending", "revoked", or null when the server predates
    /// enrolment or is unreachable (then the Access token is the only way).
    public async Task<string?> EnrollAsync(CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { key = AgentKey.Load(), name = _agentName, machine = Environment.MachineName });
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{ServerUrl}/api/agent/enroll", content, ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("status").GetString();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// Anonymous liveness: the one route a brand-new machine can reach.
    public async Task<bool> PingAnonymousAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ServerUrl}/api/agent/ping", ct);
            return resp.IsSuccessStatusCode && IsJson(resp);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// The tracked accounts a one-site server hosts; null for a legacy
    /// single-account server (no /api/accounts) or when unreachable.
    public async Task<List<TrackerAccount>?> GetAccountsAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(Keyed ? $"{ServerUrl}/api/agent/accounts" : $"{ServerUrl}/api/accounts", ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return [.. doc.RootElement.GetProperty("accounts").EnumerateArray().Select(a => new TrackerAccount(
                a.GetProperty("slug").GetString() ?? "",
                a.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                a.GetProperty("riotId").GetString() ?? "",
                a.TryGetProperty("path", out var p) ? p.GetString() : null))];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public string ServerUrl { get; }
    /// Base of every account-scoped call.
    public string Api { get; }
    public TrackerAccount? Account { get; }
    /// For logs and markers: "https://league.example [ImRA#87166]".
    public string Name => Account is { } a ? $"{ServerUrl} [{a.RiotId}]" : ServerUrl;

    /// Does this tracker's account match the player the live client says
    /// is playing? Riot ID first ("Name#TAG"), bare game name as fallback
    /// for older clients that only expose the name.
    public bool IsPlayer(string? activePlayer)
    {
        if (Account is not { } a || activePlayer is not { Length: > 0 }) return false;
        if (activePlayer.Contains('#')) return string.Equals(a.RiotId, activePlayer, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.RiotId.Split('#')[0], activePlayer, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{Api}/status", ct);
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

    /// The between-games review reel: the moments the player was in, as
    /// replay timestamps. Null when this tracker doesn't hold the match (not
    /// its account) or hasn't imported it yet - the caller retries for the
    /// latter, and this doubles as the "does this tracker own the game" probe.
    public async Task<ReviewReel?> GetReelAsync(string matchId, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{Api}/matches/{Uri.EscapeDataString(matchId)}/reel", ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return null;
            return JsonSerializer.Deserialize<ReviewReel>(await resp.Content.ReadAsStringAsync(ct), Json);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<RenderJob?> ClaimNextAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{Api}/render/next?agent={Uri.EscapeDataString(_agentName)}", null, ct);
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
            using var resp = await _http.PostAsync($"{Api}/render/release-stale?agent={Uri.EscapeDataString(_agentName)}", null, ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return [];
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return [.. doc.RootElement.GetProperty("released").EnumerateArray().Select(e => e.GetString()).OfType<string>()];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// How many render jobs sit claimable on this tracker ("pending" or
    /// "partial" - "rendering" means somebody already holds the lease).
    /// Best-effort: an unreachable or walled-off tracker counts as zero.
    public async Task<int> PendingRenderCountAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{Api}/render/queue", ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return 0;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.EnumerateArray().Count(row =>
                row.TryGetProperty("status", out var s) && s.GetString() is "pending" or "partial");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static bool IsJson(HttpResponseMessage resp) =>
        resp.Content.Headers.ContentType?.MediaType is "application/json";

    /// The tracker's defaults + secrets for its agents (AgentConfig keys ->
    /// string values). Null when unreachable or not yet redeployed with the
    /// endpoint - the local file then stands alone, as it always did.
    public async Task<Dictionary<string, string>?> GetProfileAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ServerUrl}/api/agent/profile", ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(await resp.Content.ReadAsStringAsync(ct), Json);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// Tells the tracker this agent is alive and what it is doing. Returns
    /// the newest published agent version the tracker knows of (null = none
    /// or an old tracker).
    public async Task<HeartbeatReply?> HeartbeatAsync(object beat, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(beat), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{ServerUrl}/api/agent/heartbeat", content, ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
            return new HeartbeatReply(Str("latest"), Str("command"), Str("commandToken"));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<AgentRelease?> GetReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ServerUrl}/api/agent/release", ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return null;
            return JsonSerializer.Deserialize<AgentRelease>(await resp.Content.ReadAsStringAsync(ct), Json);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task DownloadReleaseAsync(AgentRelease release, string targetPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{ServerUrl}/api/agent/release/{Uri.EscapeDataString(release.File)}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, ct);
    }

    public async Task DownloadReplayAsync(RenderJob job, string targetPath, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync($"{ServerUrl}{job.ReplayUrl}", ct);
        await using var file = File.Create(targetPath);
        await stream.CopyToAsync(file, ct);
    }

    /// The archived .rofl for a match, without a render job to carry the url -
    /// the between-games review launches replays off nothing but a match id.
    /// False = this tracker has no replay archived for it.
    public async Task<bool> TryDownloadReplayAsync(string matchId, string targetPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(
            $"{Api}/matches/{Uri.EscapeDataString(matchId)}/replay",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return false;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(targetPath);
        await stream.CopyToAsync(file, ct);
        return true;
    }

    public async Task UploadAsync(RenderJob job, int index, string mp4Path, CancellationToken ct)
    {
        var url = job.IsFullGame
            ? $"{Api}/render/{job.MatchId}/full"
            : $"{Api}/render/{job.MatchId}/clips/{index}";
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
    /// includeVideo=false is the storage-free mode: only the sidecars land
    /// on the tracker; the video goes to YouTube by hand.
    public async Task<bool> UploadVodAsync(string matchId, string mp4Path, string? metaPath, string? eventsPath, string? thumbPath, bool includeVideo, CancellationToken ct)
    {
        // Byte-cheap probe before shipping chunks: a tracker without the VOD
        // endpoints (not yet redeployed) or an unreachable one fails here,
        // not 64MB into an upload it was never going to accept.
        try
        {
            using var probe = await _http.GetAsync($"{Api}/matches/{matchId}/vod/status", ct);
            if (!probe.IsSuccessStatusCode || !IsJson(probe)) return false;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        // Chunked: Cloudflare rejects bodies over ~100MB, and a VOD runs to
        // gigabytes. 64MB pieces + a size-checked commit; the first chunk
        // answering 404 means this tracker doesn't know the match.
        const int ChunkBytes = 64 * 1024 * 1024;
        if (includeVideo)
        {
            await using var file = File.OpenRead(mp4Path);
            var buffer = new byte[ChunkBytes];
            long offset = 0;
            while (offset < file.Length)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkBytes, file.Length - offset)), ct);
                if (read == 0) break;
                using var content = new ByteArrayContent(buffer, 0, read);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var resp = await _http.PutAsync($"{Api}/vods/{matchId}/chunk?offset={offset}", content, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) return false;
                if (resp.StatusCode == HttpStatusCode.Conflict)
                {
                    // Server has a different partial length (an earlier
                    // attempt died mid-chunk) - resume from where it is.
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    offset = JsonDocument.Parse(body).RootElement.GetProperty("expected").GetInt64();
                    file.Seek(offset, SeekOrigin.Begin);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                offset += read;
            }
            using var commit = await _http.PostAsync($"{Api}/vods/{matchId}/commit?size={file.Length}", null, ct);
            commit.EnsureSuccessStatusCode();
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
            using var resp = await _http.PutAsync($"{Api}/vods/{matchId}/{name}", content, ct);
            // In sidecars-only mode the first sidecar is also the ownership
            // test: 404 = this tracker doesn't know the match.
            if (resp.StatusCode == HttpStatusCode.NotFound) return false;
            resp.EnsureSuccessStatusCode();
        }
        return true;
    }

    /// The match's already-registered YouTube link, if this tracker has one.
    /// Null = no link, unknown match, or unreachable tracker (the caller's
    /// duplicate-upload guard just doesn't trigger).
    public async Task<string?> GetVodLinkAsync(string matchId, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{Api}/matches/{matchId}/vod/status", ct);
            if (!resp.IsSuccessStatusCode || !IsJson(resp)) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("youtubeUrl", out var url) ? url.GetString() : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// Registers a match's YouTube link (the review player embeds it). False
    /// when this tracker doesn't know the match - same ownership routing as
    /// the VOD upload; the caller tries the next tracker.
    public async Task<bool> SetVodLinkAsync(string matchId, string url, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { url }),
            System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{Api}/matches/{matchId}/vod/link", content, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        resp.EnsureSuccessStatusCode();
        return true;
    }

    public async Task CompleteAsync(RenderJob job, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{Api}/render/{job.MatchId}/complete?kind={job.Kind}", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task FailAsync(RenderJob job, string error, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsync($"{Api}/render/{job.MatchId}/fail?kind={job.Kind}", new StringContent(error), ct);
        }
        catch
        {
            // Reporting the failure failed too; the lease will expire on its own.
        }
    }
}
