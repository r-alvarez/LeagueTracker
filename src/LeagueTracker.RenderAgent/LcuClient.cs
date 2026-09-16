using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

/// The League client's local API (LCU). Vanguard denies direct launches of the
/// game binary, so replays go through the client instead: drop the .rofl in
/// the client's Replays folder, ask it to scan, then watch - the client starts
/// the game itself, which Vanguard allows. Requires the client to be running
/// and logged in.
public sealed class LcuClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    private LcuClient(int port, string token)
    {
        // Self-signed local cert, same as the Replay API.
        _http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{token}")));
        _base = $"https://127.0.0.1:{port}";
    }

    /// Null when the client isn't running (no lockfile in the League root),
    /// and null after clearing a STALE one: League leaves its lockfile behind
    /// when it dies ungracefully (a force-kill, or a reboot mid-render), and
    /// the file alone is enough to make this method hand back a client that
    /// will never answer. The caller then waits for a start that is not
    /// happening ('League client still starting') and never reaches the
    /// launch path that would fix it - so the deadlock outlives every poll.
    /// The pid in the file settles it: no process, no client.
    public static LcuClient? TryConnect(string leagueRoot)
    {
        var lockfile = Path.Combine(leagueRoot, "lockfile");
        if (!File.Exists(lockfile)) return null;
        try
        {
            // name:pid:port:token:protocol - shared read; the client keeps it open.
            using var stream = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var parts = new StreamReader(stream).ReadToEnd().Split(':');
            if (parts.Length < 5) return null;
            if (!int.TryParse(parts[1], out var pid) || !IsAlive(pid))
            {
                stream.Dispose();
                ClearStale(lockfile, pid);
                return null;
            }
            return new LcuClient(int.Parse(parts[2]), parts[3]);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// Said once per stale file, not once per poll: the delete either works
    /// (next pass relaunches cleanly) or the file is still held, which means
    /// the client is alive after all and the pid check was the wrong answer.
    private static void ClearStale(string lockfile, int pid)
    {
        try
        {
            File.Delete(lockfile);
            Log.Warn($"League's lockfile pointed at dead pid {pid} - cleared it; the client can be launched again");
        }
        catch (Exception ex)
        {
            Log.Warn($"League's lockfile points at dead pid {pid} but could not be cleared ({ex.Message})");
        }
    }

    /// Asks the client to shut itself down. Politeness is the point: a
    /// graceful exit removes League's lockfile on the way out, where a kill
    /// leaves the stale file that TryConnect above has to clean up after.
    /// The request dies with the process it is closing, so a cancelled or
    /// failed send is the expected case, not an error worth reporting.
    public async Task QuitAsync(CancellationToken ct)
    {
        try { using var _ = await _http.PostAsync($"{_base}/process-control/v1/process/quit", null, ct); }
        catch { /* the client is going away - that IS the success case */ }
    }

    /// True when the client answers on its replay API (fully started, logged in).
    public async Task<bool> IsUpAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_base}/lol-replays/v1/rofls/path", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// Where the logged-in player is in the play flow: "None" when idle in
    /// the client, otherwise "Lobby", "Matchmaking", "ChampSelect",
    /// "InProgress", ... Null when the endpoint can't answer.
    public async Task<string?> GetGameflowPhaseAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_base}/lol-gameflow/v1/gameflow-phase", ct);
            return JsonSerializer.Deserialize<string>(json);
        }
        catch
        {
            return null;
        }
    }

    public sealed record GameSession(long GameId, string? PlatformId, long QueueId, string? GameMode);

    /// Identity of the game the player is currently in, from the gameflow
    /// session - known from the loading screen on, before the game process
    /// serves anything. PlatformId + GameId is the tracker-side match id.
    public async Task<GameSession?> GetGameSessionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_base}/lol-gameflow/v1/session", ct));
            if (!doc.RootElement.TryGetProperty("gameData", out var gameData)) return null;
            var gameId = gameData.TryGetProperty("gameId", out var id) ? id.GetInt64() : 0;
            var platform = gameData.TryGetProperty("platformId", out var p) ? p.GetString() : null;
            var queueId = gameData.TryGetProperty("queue", out var queue) && queue.TryGetProperty("id", out var qid)
                ? qid.GetInt64() : -1;
            var gameMode = gameData.TryGetProperty("queue", out var q2) && q2.TryGetProperty("gameMode", out var gm)
                ? gm.GetString() : null;
            // gameData.platformId is empty in practice (observed on live and
            // practice games alike) - the client's region endpoint is the
            // reliable source, mapped to the platform id match ids use.
            if (platform is not { Length: > 0 }) platform = await GetPlatformIdAsync(ct);
            return gameId > 0 ? new GameSession(gameId, platform, queueId, gameMode) : null;
        }
        catch
        {
            return null;
        }
    }

    /// Platform id ("EUW1") from the client's region ("EUW").
    public async Task<string?> GetPlatformIdAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_base}/riotclient/region-locale", ct));
            var region = doc.RootElement.TryGetProperty("region", out var r) ? r.GetString() : null;
            return region?.ToUpperInvariant() switch
            {
                "EUW" => "EUW1", "EUNE" => "EUN1", "NA" => "NA1", "BR" => "BR1",
                "LAN" => "LA1", "LAS" => "LA2", "OCE" => "OC1", "TR" => "TR1",
                "JP" => "JP1", "KR" => "KR", "RU" => "RU", "ME" => "ME1",
                "SG" => "SG2", "TW" => "TW2", "VN" => "VN2",
                { Length: > 0 } other => other, // new region: better a guess in the log than nothing
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> GetReplaysPathAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{_base}/lol-replays/v1/rofls/path", ct);
        return JsonSerializer.Deserialize<string>(json) is { Length: > 0 } path
            ? path
            : throw new InvalidOperationException("the client returned no replays path");
    }

    /// Makes the client ingest .rofl files sitting in its Replays folder -
    /// without this, watch silently does nothing (204 but no metadata).
    public async Task ScanAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{_base}/lol-replays/v1/rofls/scan", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task WatchAsync(long gameId, CancellationToken ct)
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_base}/lol-replays/v1/rofls/{gameId}/watch", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
