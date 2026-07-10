using System.Runtime.InteropServices;

namespace LeagueTracker.RenderAgent;

/// Screen rectangle of the game window, for cropping the desktop capture.
/// gdigrab's window capture BitBlts the window DC, which is black for a
/// Direct3D game - so we capture the composited desktop (ddagrab) and crop
/// to where the window actually is.
internal static class GameWindow
{
    static GameWindow()
    {
        // Physical pixels: without this, display scaling would virtualize the
        // coordinates and the crop would miss.
        SetProcessDPIAware();
    }

    public static (int X, int Y, int Width, int Height)? FindClientRect(string title)
    {
        var hwnd = FindWindowW(null, title);
        if (hwnd == 0) return null;
        if (!GetClientRect(hwnd, out var rect)) return null;
        var origin = default(NativePoint);
        if (!ClientToScreen(hwnd, ref origin)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        return width > 0 && height > 0 ? (origin.X, origin.Y, width, height) : null;
    }

    /// Time since the last keyboard/mouse input, session-wide.
    public static TimeSpan UserIdleTime
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Tick));
        }
    }

    /// Restores the game window if it was minimized - a minimized window
    /// reports a degenerate client rect, which breaks the capture crop.
    public static bool TryRestore(string title)
    {
        var hwnd = FindWindowW(null, title);
        if (hwnd == 0) return false;
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
        Thread.Sleep(600);
        return true;
    }

    /// Clicks at a position given as ratios of the game window's client area.
    /// Used to drive the replay camera/fog dropdowns, which have no working
    /// Replay API equivalent. Strictly guarded: clicks are only sent when the
    /// game window is verifiably foreground, so they can never land elsewhere.
    public static bool TryClickAt(string title, double rx, double ry)
    {
        var hwnd = FindWindowW(null, title);
        if (hwnd == 0) return false;
        // Windows only grants SetForegroundWindow to the process that owns the
        // foreground or received recent input - a synthesized Alt press counts
        // as recent input and unlocks it (the classic background-focus trick).
        keybd_event(VkMenu, 0, 0, 0);
        SetForegroundWindow(hwnd);
        keybd_event(VkMenu, 0, KeyeventfKeyup, 0);
        Thread.Sleep(400);
        if (GetForegroundWindow() != hwnd) return false;
        if (FindClientRect(title) is not { Width: > 0, Height: > 0 } rect) return false;
        SetCursorPos(rect.X + (int)(rect.Width * rx), rect.Y + (int)(rect.Height * ry));
        Thread.Sleep(120);
        mouse_event(MouseeventfLeftdown, 0, 0, 0, 0);
        Thread.Sleep(60);
        mouse_event(MouseeventfLeftup, 0, 0, 0, 0);
        return true;
    }

    /// Moves the cursor (no click) to a ratio position within the client area.
    public static void TryMoveCursor(string title, double rx, double ry)
    {
        if (FindClientRect(title) is { Width: > 0, Height: > 0 } rect)
        {
            SetCursorPos(rect.X + (int)(rect.Width * rx), rect.Y + (int)(rect.Height * ry));
        }
    }

    private const uint KeyeventfKeyup = 2;
    private const byte VkMenu = 0x12;
    private const uint MouseeventfLeftdown = 0x02;
    private const uint MouseeventfLeftup = 0x04;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extra);

    private struct LastInputInfo { public uint Size; public uint Tick; }

    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private struct NativePoint { public int X, Y; }
}
