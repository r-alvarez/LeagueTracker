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
        // Co-op vs AI fills the bot seats with the literal puuid "BOT", which
        // league-v4 answers with a 400 - nothing to look up.
        if (puuid is not { Length: > 0 } || puuid == "BOT") return [];
        if (cache.ByPuuid.TryGetValue(puuid, out var hit) && DateTime.UtcNow - hit.FetchedAtUtc < ttl)
        {
            return hit.Entries;
        }
        List<LeagueEntryDto> entries;
        try
        {
            entries = (await riot.GetLeagueEntriesAsync(puuid, ct))
                .Where(e => e.QueueType is RankMath.SoloQueueType or RankMath.FlexQueueType)
                .ToList();
        }
        catch (RiotApiException ex) when (ex.StatusCode is >= 400 and < 500 && !ex.IsAuthFailure)
        {
            // A puuid league-v4 refuses to resolve has no ranks, permanently -
            // one such participant used to abort the whole match and put it on
            // the poller's retry-forever list. Transient faults still propagate
            // so the match is retried on the next pass.
            entries = [];
        }
        cache.ByPuuid[puuid] = new RankCache.Entry(DateTime.UtcNow, entries);
        return entries;
    }
}
