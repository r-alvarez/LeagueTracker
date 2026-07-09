namespace LeagueTracker.Api.Services;

/// In-memory claim ledger for render jobs. Deliberately not persisted: a
/// restart just re-offers unfinished jobs, and re-rendering is idempotent
/// because uploads overwrite by window index.
public sealed class RenderLeaseService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime ExpiresUtc, string Agent)> _leases = [];

    public bool TryClaim(string matchId, string agent)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(matchId, out var lease) && lease.ExpiresUtc > DateTime.UtcNow) return false;
            _leases[matchId] = (DateTime.UtcNow + LeaseDuration, agent);
            return true;
        }
    }

    public bool IsLeased(string matchId)
    {
        lock (_gate) return _leases.TryGetValue(matchId, out var lease) && lease.ExpiresUtc > DateTime.UtcNow;
    }

    public void Release(string matchId)
    {
        lock (_gate) _leases.Remove(matchId);
    }
}
