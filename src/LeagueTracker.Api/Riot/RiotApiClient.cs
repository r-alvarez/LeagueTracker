using System.Text.Json;
using LeagueTracker.Api.Accounts;

namespace LeagueTracker.Api.Riot;

// Routing follows the bound account (region/platform); the key and the
// rate limiter are shared by every account, as they are on Riot's side.
public sealed class RiotApiClient(HttpClient http, AccountContext account)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private string RegionalBase => $"https://{account.Current.Region}.api.riotgames.com";
    private string PlatformBase => $"https://{account.Current.Platform}.api.riotgames.com";

    public async Task<AccountDto> GetAccountAsync(string gameName, string tagLine, CancellationToken ct) =>
        JsonSerializer.Deserialize<AccountDto>(
            await GetStringAsync($"{RegionalBase}/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(gameName)}/{Uri.EscapeDataString(tagLine)}", "account-v1/accounts/by-riot-id", ct), Json)!;

    // The current Riot ID for a puuid - the alias check after a rename.
    public async Task<AccountDto> GetAccountByPuuidAsync(string puuid, CancellationToken ct) =>
        JsonSerializer.Deserialize<AccountDto>(
            await GetStringAsync($"{RegionalBase}/riot/account/v1/accounts/by-puuid/{puuid}", "account-v1/accounts/by-puuid", ct), Json)!;

    // Profile icon (the ownership claim's proof) - summoner-v4 by puuid.
    public async Task<SummonerDto> GetSummonerByPuuidAsync(string puuid, CancellationToken ct) =>
        JsonSerializer.Deserialize<SummonerDto>(
            await GetStringAsync($"{PlatformBase}/lol/summoner/v4/summoners/by-puuid/{puuid}", "summoner-v4/summoners/by-puuid", ct), Json)!;

    public async Task<List<string>> GetMatchIdsAsync(string puuid, int start, int count, bool rankedOnly, CancellationToken ct)
    {
        var typeFilter = rankedOnly ? "type=ranked&" : "";
        var raw = await GetStringAsync($"{RegionalBase}/lol/match/v5/matches/by-puuid/{puuid}/ids?{typeFilter}start={start}&count={count}", "match-v5/matches/by-puuid/ids", ct);
        return JsonSerializer.Deserialize<List<string>>(raw, Json) ?? [];
    }

    public Task<string> GetMatchRawAsync(string matchId, CancellationToken ct) =>
        GetStringAsync($"{RegionalBase}/lol/match/v5/matches/{matchId}", $"match-v5/matches/{matchId}", ct);

    public Task<string> GetTimelineRawAsync(string matchId, CancellationToken ct) =>
        GetStringAsync($"{RegionalBase}/lol/match/v5/matches/{matchId}/timeline", $"match-v5/matches/{matchId}/timeline", ct);

    public async Task<List<LeagueEntryDto>> GetLeagueEntriesAsync(string puuid, CancellationToken ct)
    {
        var raw = await GetStringAsync($"{PlatformBase}/lol/league/v4/entries/by-puuid/{puuid}", "league-v4/entries/by-puuid", ct);
        return JsonSerializer.Deserialize<List<LeagueEntryDto>>(raw, Json) ?? [];
    }

    // The player's challenge standings vs the ladder (per-challenge level + percentile).
    public Task<string> GetChallengesPlayerDataRawAsync(string puuid, CancellationToken ct) =>
        GetStringAsync($"{PlatformBase}/lol/challenges/v1/player-data/{puuid}", "challenges-v1/player-data", ct);

    // Challenge metadata (id -> localized name/description). Static-ish; cache it.
    public Task<string> GetChallengesConfigRawAsync(CancellationToken ct) =>
        GetStringAsync($"{PlatformBase}/lol/challenges/v1/challenges/config", "challenges-v1/challenges/config", ct);

    // Per-challenge share of the playerbase at each level (id -> level -> fraction).
    // Ladder-wide, so it moves slowly; cache it like the config.
    public Task<string> GetChallengesAllPercentilesRawAsync(CancellationToken ct) =>
        GetStringAsync($"{PlatformBase}/lol/challenges/v1/challenges/percentiles", "challenges-v1/challenges/percentiles", ct);

    // The game the player is in right now, or null - spectator 404s between games.
    public async Task<string?> GetActiveGameRawAsync(string puuid, CancellationToken ct)
    {
        try
        {
            return await GetStringAsync($"{PlatformBase}/lol/spectator/v5/active-games/by-summoner/{puuid}", "spectator-v5/active-games/by-summoner", ct);
        }
        catch (RiotApiException ex) when (ex.StatusCode is 404)
        {
            return null;
        }
    }

    // Pre-signed download URLs for the .rofl files of the player's most recent
    // games (Riot serves only the last ~5, and the links expire after an hour).
    public async Task<List<string>> GetReplayUrlsAsync(string puuid, CancellationToken ct)
    {
        var raw = await GetStringAsync($"{RegionalBase}/lol/match/v5/matches/by-puuid/{puuid}/replays", "match-v5/matches/by-puuid/replays", ct);
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("matchFileURLs", out var urls)) return [];
        return [.. urls.EnumerateArray().Select(u => u.GetString()).OfType<string>()];
    }

    // endpoint names the route without its ids: the exception message ends up
    // in logs, and puuids do not belong there (audit F10).
    private async Task<string> GetStringAsync(string url, string endpoint, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new RiotApiException((int)resp.StatusCode, endpoint, await resp.Content.ReadAsStringAsync(ct));
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }
}

public sealed class RiotApiException(int statusCode, string endpoint, string body)
    : Exception($"Riot API returned HTTP {statusCode} for {endpoint}: {Truncate(body)}")
{
    public int StatusCode { get; } = statusCode;
    public bool IsAuthFailure => StatusCode is 401 or 403;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
