namespace LeagueTracker.Api.Services;

/// Progress of the single long-running background job (history sync or import).
/// One at a time is plenty for a single-user tool.
public sealed class JobStatusService
{
    private readonly Lock _lock = new();

    public string? JobName { get; private set; }
    public bool Running { get; private set; }
    public int Processed { get; private set; }
    public int Total { get; private set; }
    public string Message { get; private set; } = "";
    public DateTime? StartedUtc { get; private set; }

    public bool TryStart(string jobName)
    {
        lock (_lock)
        {
            if (Running) return false;
            JobName = jobName;
            Running = true;
            Processed = 0;
            Total = 0;
            Message = "starting";
            StartedUtc = DateTime.UtcNow;
            return true;
        }
    }

    public void Report(int processed, int total, string message)
    {
        lock (_lock)
        {
            Processed = processed;
            Total = total;
            Message = message;
        }
    }

    public void Finish(string message)
    {
        lock (_lock)
        {
            Running = false;
            Message = message;
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new { JobName, Running, Processed, Total, Message, StartedUtc };
        }
    }
}
