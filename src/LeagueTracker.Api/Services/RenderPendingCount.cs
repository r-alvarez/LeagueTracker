using LeagueTracker.Api.Accounts;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

// The waker asks "is render work waiting anywhere" every minute; the honest
// answer opens every account's schema and plans every archived match, so one
// answer serves everyone who asks within the minute, and only one asker
// computes it at a time.
public sealed class RenderPendingCount(AccountRegistry registry, AccountScopes scopes, AccountInitializer initializer,
    PatchReference patchReference, IOptions<MediaRetentionOptions> retention)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (int Pending, DateTime AtUtc)? _last;

    public async Task<int> GetAsync(CancellationToken ct)
    {
        if (_last is { } cached && DateTime.UtcNow - cached.AtUtc < Fresh) return cached.Pending;
        await _gate.WaitAsync(ct);
        try
        {
            if (_last is { } raced && DateTime.UtcNow - raced.AtUtc < Fresh) return raced.Pending;
            var pending = await CountAsync(ct);
            _last = (pending, DateTime.UtcNow);
            return pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> CountAsync(CancellationToken ct)
    {
        var pending = 0;
        var patches = await patchReference.PatchesNewestFirstAsync(ct);
        foreach (var account in registry.All.Where(initializer.IsReady))
        {
            using var scope = scopes.Create(account);
            var leases = scope.ServiceProvider.GetRequiredService<RenderLeaseService>();
            var clips = scope.ServiceProvider.GetRequiredService<ClipService>();
            var rows = (await clips.QueueAsync(leases, ct))
                .Concat(await scope.ServiceProvider.GetRequiredService<FullGameService>().QueueRowsAsync(leases, ct));
            // The queue rows are the anonymous shapes the Data page renders; the
            // status is the one field this needs.
            pending += rows.Count(r => System.Text.Json.JsonSerializer.SerializeToElement(r).GetProperty("Status").GetString() is "pending" or "partial");
            // A replay still to fetch is work too: the box has to wake and open
            // its client to fetch it, and only then does the render job exist.
            pending += (await clips.WantedReplaysAsync(patches, retention.Value.ReplayPatches, ct)).Count;
        }
        return pending;
    }
}
