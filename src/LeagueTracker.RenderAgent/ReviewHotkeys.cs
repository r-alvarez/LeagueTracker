using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LeagueTracker.RenderAgent;

/// The review session's remote control: F8/F9/F10 while the replay has
/// the screen. A low-level hook rather than RegisterHotKey because the game
/// holds focus (often fullscreen) and the agent has no window of its own -
/// the same reason InputLogger uses one.
///
/// Deliberately narrow: it observes three function keys and swallows nothing
/// (every key still reaches the game), so it can never interfere with typing
/// or with the game's own bindings. It exists only for the length of a review
/// session - no hook survives it.
public sealed class ReviewHotkeys : IDisposable
{
    public enum Command { Next, Previous, Repeat }

    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const uint VkF8 = 0x77;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;

    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly Thread _hookThread;
    private HookProc? _proc;
    private uint _hookThreadId;

    private ReviewHotkeys()
    {
        _hookThread = new Thread(HookPump) { IsBackground = true, Name = "review-hotkeys" };
        _hookThread.Start();
    }

    /// Null when the hook won't install - the session then falls back to
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

    private void HookPump()
    {
        _hookThreadId = GetCurrentThreadId();
        _proc = KeyboardHook;
        var hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
        if (hook == 0)
        {
            Log.Warn("Review hotkeys: could not install the keyboard hook");
            return;
        }
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UnhookWindowsHookEx(hook);
    }

    private nint KeyboardHook(int code, nuint wParam, nint lParam)
    {
        if (code >= 0 && wParam is WmKeydown or WmSyskeydown)
        {
            // Callbacks run on the hook thread and must never block - enqueue
            // and get out, exactly like InputLogger.
            switch ((uint)Marshal.ReadInt32(lParam))   // KBDLLHOOKSTRUCT.vkCode
            {
                case VkF9: _commands.Enqueue(Command.Next); break;
                case VkF8: _commands.Enqueue(Command.Previous); break;
                case VkF10: _commands.Enqueue(Command.Repeat); break;
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookThreadId != 0) PostThreadMessageW(_hookThreadId, 0x0012 /* WM_QUIT */, 0, 0);
        _hookThread.Join(TimeSpan.FromSeconds(2));
    }

    private delegate nint HookProc(int code, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int hookId, HookProc proc, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg msg, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }
}
