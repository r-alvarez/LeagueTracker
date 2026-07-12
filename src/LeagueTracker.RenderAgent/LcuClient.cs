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

    /// Null when the client isn't running (no lockfile in the League root).
    public static LcuClient? TryConnect(string leagueRoot)
    {
        var lockfile = Path.Combine(leagueRoot, "lockfile");
        if (!File.Exists(lockfile)) return null;
        try
        {
            // name:pid:port:token:protocol - shared read; the client keeps it open.
            using var stream = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var parts = new StreamReader(stream).ReadToEnd().Split(':');
            return parts.Length >= 5 ? new LcuClient(int.Parse(parts[2]), parts[3]) : null;
        }
        catch
        {
            return null;
        }
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
