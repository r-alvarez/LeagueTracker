using System.Text.Json;

namespace LeagueTracker.Api.Services;

// Retention reads "current patch" from Riot's list, never from an account's
// own last match: an idle account must expire against the live game.
public sealed class PatchReference(IHttpClientFactory http, ILogger<PatchReference> log)
{
    public const string HttpClientName = "ddragon";
    private const string VersionsUrl = "https://ddragon.leagueoflegends.com/api/versions.json";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string>? _patches;
    private DateTime _fetchedUtc;

    // Null until Data Dragon has answered once: callers skip patch-based
    // expiry rather than guess. A stale list beats a failed refresh.
    public async Task<IReadOnlyList<string>?> PatchesNewestFirstAsync(CancellationToken ct)
    {
        if (_patches is not null && DateTime.UtcNow - _fetchedUtc < Ttl) return _patches;
        await _gate.WaitAsync(ct);
        try
        {
            if (_patches is not null && DateTime.UtcNow - _fetchedUtc < Ttl) return _patches;
            using var client = http.CreateClient(HttpClientName);
            var versions = await client.GetFromJsonAsync<List<string>>(VersionsUrl, ct) ?? [];
            var patches = Reduce(versions);
            if (patches is { Count: > 0 })
            {
                _patches = patches;
                _fetchedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Patch list fetch failed ({Message}); {State}", ex.Message,
                _patches is null ? "patch-based expiry waits for the next pass" : "serving the last known list");
        }
        finally
        {
            _gate.Release();
        }
        return _patches;
    }

    public static IReadOnlyList<string> Reduce(IEnumerable<string> versions) =>
        [.. versions.Select(PatchOf).Where(p => p.Length > 0).Distinct()];

    // match-v5 says "16.18.712.1234", Data Dragon says "16.18.1": both are 16.18.
    public static string PatchOf(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _)
            ? $"{parts[0]}.{parts[1]}"
            : "";
    }

    // A version the list does not carry reads as current: Data Dragon
    // publishes hours after a patch goes live, and unknown must never mean delete.
    public static int PatchAge(IReadOnlyList<string> patchesNewestFirst, string gameVersion)
    {
        var patch = PatchOf(gameVersion);
        if (patch.Length is 0) return 0;
        var index = patchesNewestFirst.ToList().IndexOf(patch);
        return index < 0 ? 0 : index;
    }
}
