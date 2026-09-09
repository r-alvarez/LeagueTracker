namespace LeagueTracker.Api.Services;

// What /readyz asks the poller: has a pass finished lately. A wedged loop
// (one hung socket holds the whole pass, audit A1) used to keep receiving
// traffic with nothing to show for it.
public sealed class PollerHeartbeat
{
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime? _lastPassUtc;

    public DateTime? LastPassUtc => _lastPassUtc;

    public void PassCompleted() => _lastPassUtc = DateTime.UtcNow;

    // Before the first pass the process is still warming up; the grace is the
    // same window a pass is allowed to take.
    public bool IsFresh => (_lastPassUtc ?? _startedUtc) > DateTime.UtcNow - Stale;
}
