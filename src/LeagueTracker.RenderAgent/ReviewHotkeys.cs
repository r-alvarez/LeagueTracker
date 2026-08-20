using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LeagueTracker.RenderAgent;

/// The review session's remote control: F8/F9/F10/F12 while the replay has
/// the screen. GetAsyncKeyState polling rather than a low-level hook: the
/// review runs exactly while recording finalize and uploads saturate the
/// machine, and Windows silently removes a hook whose callback gets starved
/// past the low-level hook timeout - the keys then die for the rest of the
/// session with nothing logged (the field failure of 2026-08-19). Polling
/// cannot be deregistered, and at 40ms a real press (~100ms held) cannot
/// fall between samples.
///
/// Deliberately narrow: it observes four function keys and swallows nothing
/// (every key still reaches the game), so it can never interfere with typing
/// or with the game's own bindings. It exists only for the length of a review
/// session - the thread ends with it.
public sealed class ReviewHotkeys : IDisposable
{
    public enum Command { Next, Previous, Repeat, End }

    private static readonly (int Vk, Command Command)[] Keys =
    [
        (0x78, Command.Next),      // F9
        (0x77, Command.Previous),  // F8
        (0x79, Command.Repeat),    // F10
        (0x7B, Command.End),       // F12
    ];

    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    private ReviewHotkeys()
    {
        _thread = new Thread(Poll)
        {
            IsBackground = true,
            Name = "review-hotkeys",
            // The whole point of polling is surviving a loaded machine
            // (finalize's ffmpeg, uploads); don't get starved with it.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// Null when polling won't start - the session then falls back to
    /// advancing on its own rather than waiting forever for a key that can
    /// never arrive.
    public static ReviewHotkeys? TryStart()
    {
        try
        {
            return new ReviewHotkeys();
        }
        catch (Exception ex)
        {
            Log.Warn($"Review hotkeys unavailable: {ex.Message}");
            return null;
        }
    }

    /// The next pressed command, or null if none is waiting.
    public Command? TryDequeue() => _commands.TryDequeue(out var command) ? command : null;

    /// Drops anything pressed before now - stops a key mashed during a seek
    /// from skipping the moment it was meant to start.
    public void Drain()
    {
        while (_commands.TryDequeue(out _)) { }
    }

    private void Poll()
    {
        var down = new bool[Keys.Length];
        while (!_cts.IsCancellationRequested)
        {
            for (var i = 0; i < Keys.Length; i++)
            {
                // High bit = held right now; enqueue on the up->down edge only.
                var pressed = (GetAsyncKeyState(Keys[i].Vk) & 0x8000) != 0;
                if (pressed && !down[i]) _commands.Enqueue(Keys[i].Command);
                down[i] = pressed;
            }
            Thread.Sleep(40);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
