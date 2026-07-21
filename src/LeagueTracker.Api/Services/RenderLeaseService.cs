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

    /// Every lease held by the named agent, released. For agent startup:
    /// only one agent exists per design (two would fight over the game
    /// client), so a fresh start under the same name means the previous
    /// holder died and its claims will never complete - without this, an
    /// interrupted job sits "rendering" until the lease expires.
    public List<string> ReleaseAgent(string agent)
    {
        lock (_gate)
        {
            var held = _leases.Where(kv => kv.Value.Agent == agent).Select(kv => kv.Key).ToList();
            foreach (var key in held) _leases.Remove(key);
            return held;
        }
    }
}
