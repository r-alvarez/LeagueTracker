using System.Text;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

/// Riot's official Replay API, served by the game itself while a replay runs
/// (needs EnableReplayApi=1 in game.cfg). Self-signed cert, hence the bypass.
public sealed class ReplayApiClient : IDisposable
{
    private const string Base = "https://127.0.0.1:2999";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    public sealed record Playback(double Time, bool Paused, bool Seeking, double Speed, double Length);

    public async Task<Playback?> GetPlaybackAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _http.GetStringAsync($"{Base}/replay/playback", ct);
            return JsonSerializer.Deserialize<Playback>(raw, Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;   // game still loading, or gone
        }
    }

    public Task SetPlaybackAsync(double? time, bool? paused, double? speed, CancellationToken ct) =>
        PostAsync("/replay/playback", new { time, paused, speed }, ct);

    /// Locks the camera onto the tracked player, with fog of war from their
    /// team's view, and hides the replay UI (timeline/controls) so recordings
    /// look like a spectate rather than a replay session.
    public Task FollowPlayerAsync(string playerName, CancellationToken ct) =>
        PostAsync("/replay/render", new
        {
            cameraAttached = true,
            selectionName = playerName,
            fogOfWar = true,
            interfaceTimeline = false,
            interfaceReplay = false,
        }, ct);

    /// The selection the game actually accepted - a name the game doesn't
    /// recognise leaves this empty, so callers can verify their follow stuck.
    public async Task<string?> GetSelectionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{Base}/replay/render", ct));
            return doc.RootElement.TryGetProperty("selectionName", out var name) ? name.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// Name candidates for the tracked player, from the game's own player list
    /// - the ground truth for what selectionName will accept. Only entries that
    /// match the tracked player's name or champion qualify, so a wrong guess
    /// can never land the camera on someone else.
    public async Task<List<string>> GetCameraCandidatesAsync(string? playerName, string? champion, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{Base}/liveclientdata/playerlist", ct));
            var scored = new List<(int Score, string Name)>();
            foreach (var player in doc.RootElement.EnumerateArray())
            {
                var names = ((string[])["summonerName", "riotId", "riotIdGameName"])
                    .Select(field => player.TryGetProperty(field, out var v) ? v.GetString() : null)
                    .OfType<string>().Where(n => n.Length > 0).ToList();
                var score =
                    (champion is { Length: > 0 } && player.TryGetProperty("championName", out var champ)
                        && string.Equals(champ.GetString(), champion, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                    + (playerName is { Length: > 0 } && names.Any(n => n.StartsWith(playerName, StringComparison.OrdinalIgnoreCase)) ? 1 : 0);
                if (score > 0) scored.AddRange(names.Select(n => (score, n)));
            }
            return [.. scored.OrderByDescending(s => s.Score).Select(s => s.Name).Distinct()];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    private async Task PostAsync(string path, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{Base}{path}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
