using System.Collections.Concurrent;
using LeagueTracker.Api.Riot;

namespace LeagueTracker.Api.Services;

/// Singleton cache state, kept separate from the scoped lookup service so the
/// short-lived typed HttpClient is never captured beyond its scope.
public sealed class RankCache
{
    internal sealed record Entry(DateTime FetchedAtUtc, List<LeagueEntryDto> Entries);

    internal ConcurrentDictionary<string, Entry> ByPuuid { get; } = new();
}

/// League-V4 entries per puuid with a TTL cache - the same players recur across
/// games, and their rank barely moves between two of yours.
public sealed class RankLookupService(RiotApiClient riot, RankCache cache)
{
    public async Task<List<LeagueEntryDto>> GetEntriesAsync(string puuid, TimeSpan ttl, CancellationToken ct)
    {
        if (cache.ByPuuid.TryGetValue(puuid, out var hit) && DateTime.UtcNow - hit.FetchedAtUtc < ttl)
        {
            return hit.Entries;
        }
        var entries = (await riot.GetLeagueEntriesAsync(puuid, ct))
            .Where(e => e.QueueType is RankMath.SoloQueueType or RankMath.FlexQueueType)
            .ToList();
        cache.ByPuuid[puuid] = new RankCache.Entry(DateTime.UtcNow, entries);
        return entries;
    }
}
