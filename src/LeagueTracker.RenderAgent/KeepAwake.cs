using System.Runtime.InteropServices;

namespace LeagueTracker.RenderAgent;

/// Keeps Windows' idle-sleep timers at bay while unattended work runs. The
/// machine sleeps on an idle timer (the NAS wakes it when render work
/// queues), and renders, recordings and uploads generate no input - without
/// these pulses the PC dozes off mid-job. Doubly so after a Wake-on-LAN
/// wake: with no user input Windows re-sleeps on the UNATTENDED idle
/// timeout, which defaults to two minutes - shorter than a League client
/// takes to come up.
///
/// Pulse-based (ES_SYSTEM_REQUIRED without ES_CONTINUOUS, every 50s)
/// because await hops threads and a CONTINUOUS assertion belongs to the
/// thread that made it - it would linger or vanish with the wrong one.
public static class KeepAwake
{
    private const uint EsSystemRequired = 0x00000001;

    private static readonly object Gate = new();
    private static readonly TimeSpan PulseEvery = TimeSpan.FromSeconds(50);
    private static readonly Timer Pulse = new(_ => OnPulse());
    private static int _holds;
    private static DateTime _holdUntilUtc = DateTime.MinValue;

    /// Keep the machine awake until the returned handle is disposed - for
    /// work with a clear owner and end (a render job, a recording session).
    public static IDisposable Hold()
    {
        lock (Gate)
        {
            _holds++;
            Kick();
        }
        return new Release();
    }

    /// Keep the machine awake for a fixed window - for work whose end nobody
    /// owns, like a just-launched client booting toward its first claim.
    /// Windows extend, never shorten.
    public static void HoldFor(TimeSpan window)
    {
        lock (Gate)
        {
            var until = DateTime.UtcNow + window;
            if (until > _holdUntilUtc) _holdUntilUtc = until;
            Kick();
        }
    }

    private static void Kick()
    {
        SetThreadExecutionState(EsSystemRequired);
        Pulse.Change(PulseEvery, PulseEvery);
    }

    private static void OnPulse()
    {
        lock (Gate)
        {
            if (_holds > 0 || DateTime.UtcNow < _holdUntilUtc)
            {
                SetThreadExecutionState(EsSystemRequired);
            }
            else
            {
                // Nothing held: stop pulsing and let the idle timer run out.
                Pulse.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private sealed class Release : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (Gate) _holds--;
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
