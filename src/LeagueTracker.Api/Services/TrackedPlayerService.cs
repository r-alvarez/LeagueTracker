using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

public sealed class TrackedPlayerService(LeagueDbContext db, RiotApiClient riot, IOptions<RiotOptions> options)
{
    private readonly RiotOptions _options = options.Value;

    public string RiotId => $"{_options.GameName}#{_options.TagLine}";

    private string CacheKey => $"puuid:{RiotId}";

    /// Puuid is stable per account+region; cache it in the db so scheduled/service
    /// startups don't spend a request on account-v1 every time.
    public async Task<string> GetPuuidAsync(CancellationToken ct)
    {
        if (await db.KeyValues.FindAsync([CacheKey], ct) is { } cached) return cached.Value;

        var account = await riot.GetAccountAsync(_options.GameName, _options.TagLine, ct);
        await StorePuuidAsync(account.Puuid, ct);
        return account.Puuid;
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
