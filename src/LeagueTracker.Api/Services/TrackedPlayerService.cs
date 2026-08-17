using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;

namespace LeagueTracker.Api.Services;

public sealed class TrackedPlayerService(LeagueDbContext db, RiotApiClient riot, AccountContext account)
{
    public Account Account => account.Current;
    public string RiotId => Account.RiotId;

    public static string PuuidCacheKey(string riotId) => $"puuid:{riotId}";
    private string CacheKey => PuuidCacheKey(RiotId);

    /// Puuid is stable per account+region; the registry holds it, the account's
    /// own db keeps a copy (imports work before the registry knew the account),
    /// and account-v1 is asked only when neither has it.
    public async Task<string> GetPuuidAsync(CancellationToken ct)
    {
        if (Account.Puuid is { Length: > 0 } known) return known;
        if (await db.KeyValues.FindAsync([CacheKey], ct) is { } cached)
        {
            account.Registry.Update(Account, a => a.Puuid ??= cached.Value);
            return cached.Value;
        }

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
        if (Account.Puuid != puuid && account.Registry.ByPuuid(puuid) is null) account.Registry.Update(Account, a => a.Puuid = puuid);
    }
}
