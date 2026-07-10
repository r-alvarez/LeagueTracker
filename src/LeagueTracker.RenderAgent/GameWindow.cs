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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private struct NativePoint { public int X, Y; }
}
