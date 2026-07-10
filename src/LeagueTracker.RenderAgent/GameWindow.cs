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

    /// Presses a key in the game window - used for the camera-lock toggle (Y),
    /// which the Replay API has no working equivalent for. Strictly guarded:
    /// the key is only sent when the game window is verifiably foreground, so
    /// it can never land in another application.
    public static bool TryPressKey(string title, byte virtualKey)
    {
        var hwnd = FindWindowW(null, title);
        if (hwnd == 0) return false;
        SetForegroundWindow(hwnd);
        Thread.Sleep(400);
        if (GetForegroundWindow() != hwnd) return false;
        keybd_event(virtualKey, 0, 0, 0);
        Thread.Sleep(60);
        keybd_event(virtualKey, 0, KeyeventfKeyup, 0);
        return true;
    }

    private const uint KeyeventfKeyup = 2;

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

    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private struct NativePoint { public int X, Y; }
}
