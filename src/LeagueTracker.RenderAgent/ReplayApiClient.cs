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

    /// Selects the tracked player: fog of war renders from their team's view
    /// and the target frame shows their abilities/cooldowns. The UI state is
    /// asserted explicitly every time because the game persists replay UI
    /// settings across sessions - whatever the last session (or a human
    /// experimenting) left behind would silently leak into recordings.
    /// interfaceFrames in particular replaces the target frame (and its
    /// cooldowns) with spectate-style side frames, which defeats the point.
    public Task FollowPlayerAsync(string playerName, CancellationToken ct) =>
        PostAsync("/replay/render", new
        {
            selectionName = playerName,
            fogOfWar = true,
            interfaceFrames = false,
            interfaceTarget = true,
            interfaceReplay = true,
            interfaceTimeline = true,
        }, ct);

    /// The replay's player list in slot order (0-9, matching the camera
    /// dropdown's champion entries): each player's champion and whether
    /// they're on the first team (blue). Champions are unique per game in
    /// ranked/normals. Empty while the game is still loading the list.
    public async Task<List<(string Champion, bool Blue)>> GetPlayerListAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{Base}/liveclientdata/playerlist", ct));
            var players = new List<(string, bool)>();
            foreach (var player in doc.RootElement.EnumerateArray())
            {
                var champ = player.TryGetProperty("championName", out var c) ? c.GetString() ?? "" : "";
                var blue = !player.TryGetProperty("team", out var team) || team.GetString() == "ORDER";
                players.Add((champ, blue));
            }
            return players;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// Riot has two names per champion: the internal id that match data (and
    /// therefore the job's MyChampion) uses ("MissFortune", "MonkeyKing") and
    /// the display name the replay's player list reports ("Miss Fortune",
    /// "Wukong"). Compare on letters/digits only and map the ids that differ
    /// outright, or every multi-word champion becomes unrenderable.
    public static bool ChampionMatches(string? a, string? b)
    {
        return a is { Length: > 0 } && b is { Length: > 0 } && Canon(a) == Canon(b);

        static string Canon(string name)
        {
            var canon = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
            return canon switch
            {
                "monkeyking" => "wukong",
                "nunu" => "nunuwillump",
                "renata" => "renataglasc",
                _ => canon,
            };
        }
    }

    /// Parks the free camera at a reference point ahead of the dropdown
    /// clicks, re-asserting the selection (some render writes clear it).
    /// Returns where the camera actually ended up - the game may ignore or
    /// clamp the position, and the tracking check must measure from reality,
    /// not intent. Best-effort: a failed write just leaves the camera (and
    /// the returned reference) wherever the world reload put it.
    public async Task<(double X, double Z)?> ParkCameraAsync(double x, double y, double z, string? selectionName, CancellationToken ct)
    {
        try
        {
            object body = selectionName is { Length: > 0 }
                ? new { cameraPosition = new { x, y, z }, selectionName }
                : new { cameraPosition = new { x, y, z } };
            await PostAsync("/replay/render", body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
        await Task.Delay(300, ct);
        return await GetCameraPositionAsync(ct);
    }

    /// Camera world position - the ground truth for whether a camera lock is
    /// actually tracking (the position moves) or just claimed.
    public async Task<(double X, double Z)?> GetCameraPositionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{Base}/replay/render", ct));
            var pos = doc.RootElement.GetProperty("cameraPosition");
            return (pos.GetProperty("x").GetDouble(), pos.GetProperty("z").GetDouble());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or KeyNotFoundException)
        {
            return null;
        }
    }

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
                    (player.TryGetProperty("championName", out var champ)
                        && ChampionMatches(champ.GetString(), champion) ? 2 : 0)
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
