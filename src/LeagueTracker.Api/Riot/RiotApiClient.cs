using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Riot;

public sealed class RiotApiClient(HttpClient http, IOptions<RiotOptions> options)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly RiotOptions _options = options.Value;

    private string RegionalBase => $"https://{_options.Region}.api.riotgames.com";
    private string PlatformBase => $"https://{_options.Platform}.api.riotgames.com";

    public async Task<AccountDto> GetAccountAsync(string gameName, string tagLine, CancellationToken ct) =>
        JsonSerializer.Deserialize<AccountDto>(
            await GetStringAsync($"{RegionalBase}/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(gameName)}/{Uri.EscapeDataString(tagLine)}", ct), Json)!;

    public async Task<List<string>> GetMatchIdsAsync(string puuid, int start, int count, bool rankedOnly, CancellationToken ct)
    {
        var typeFilter = rankedOnly ? "type=ranked&" : "";
        var raw = await GetStringAsync($"{RegionalBase}/lol/match/v5/matches/by-puuid/{puuid}/ids?{typeFilter}start={start}&count={count}", ct);
        return JsonSerializer.Deserialize<List<string>>(raw, Json) ?? [];
    }

    public Task<string> GetMatchRawAsync(string matchId, CancellationToken ct) =>
        GetStringAsync($"{RegionalBase}/lol/match/v5/matches/{matchId}", ct);

    public Task<string> GetTimelineRawAsync(string matchId, CancellationToken ct) =>
        GetStringAsync($"{RegionalBase}/lol/match/v5/matches/{matchId}/timeline", ct);

    public async Task<List<LeagueEntryDto>> GetLeagueEntriesAsync(string puuid, CancellationToken ct)
    {
        var raw = await GetStringAsync($"{PlatformBase}/lol/league/v4/entries/by-puuid/{puuid}", ct);
        return JsonSerializer.Deserialize<List<LeagueEntryDto>>(raw, Json) ?? [];
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new RiotApiException((int)resp.StatusCode, url, await resp.Content.ReadAsStringAsync(ct));
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }
}

public sealed class RiotApiException(int statusCode, string url, string body)
    : Exception($"Riot API returned HTTP {statusCode} for {url}: {Truncate(body)}")
{
    public int StatusCode { get; } = statusCode;
    public bool IsAuthFailure => StatusCode is 401 or 403;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
