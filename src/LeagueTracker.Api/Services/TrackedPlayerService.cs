using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;

namespace LeagueTracker.Api.Services;

public sealed class TrackedPlayerService(LeagueDbContext db, RiotApiClient riot, AccountContext account)
{
    public Account Account => account.Current;
    public string RiotId => Account.RiotId;

    private string CacheKey => $"puuid:{RiotId}";

    /// Puuid is stable per account+region; cache it in the db so scheduled/service
    /// startups don't spend a request on account-v1 every time.
    public async Task<string> GetPuuidAsync(CancellationToken ct)
    {
        if (await db.KeyValues.FindAsync([CacheKey], ct) is { } cached) return cached.Value;

        var resolved = await riot.GetAccountAsync(Account.GameName, Account.TagLine, ct);
        await StorePuuidAsync(resolved.Puuid, ct);
        return resolved.Puuid;
    }

    /// Lets the importer seed the puuid it inferred from export files, so imports
    /// work before any API key is configured.
    public async Task StorePuuidAsync(string puuid, CancellationToken ct)
    {
        if (await db.KeyValues.FindAsync([CacheKey], ct) is { } existing)
        {
            existing.Value = puuid;
        }
        else
        {
            db.KeyValues.Add(new KeyValue { Key = CacheKey, Value = puuid });
        }
        await db.SaveChangesAsync(ct);
    }
}
