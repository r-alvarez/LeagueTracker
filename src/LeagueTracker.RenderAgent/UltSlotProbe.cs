using System.Globalization;
using System.Runtime.InteropServices;

namespace LeagueTracker.RenderAgent;

/// Reads how bright the R slot on the live HUD is, a few times a second, so
/// the review can tell an ult that was cast (the slot goes dark under the
/// cooldown veil) from R pressed with the ult down. A 52px screen read, not a
/// video decode: decoding the recording afterwards costs ~5 minutes of a
/// 1440p game, on the machine the next game is about to be played on.
///
/// The slot's place depends on the HUD scale setting. Only the scale it was
/// measured at is known (GlobalScale 0.30: R centred on the screen, 105px up
/// at 1440p, scaling with the window height); anything else is skipped, and
/// the review falls back to R presses alone.
public sealed class UltSlotProbe : IDisposable
{
    private const double CalibratedScale = 0.30;
    private readonly int _x, _y, _size;
    private readonly nint _screen, _memory, _bitmap, _previous, _bits;

    private UltSlotProbe(int x, int y, int size)
    {
        (_x, _y, _size) = (x, y, size);
        _screen = GetDC(0);
        _memory = CreateCompatibleDC(_screen);
        var info = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = size, Height = -size, // top-down
            Planes = 1, BitCount = 32,
        };
        _bitmap = CreateDIBSection(_memory, ref info, 0, out _bits, 0, 0);
        if (_bitmap == 0) throw new InvalidOperationException("CreateDIBSection failed");
        _previous = SelectObject(_memory, _bitmap);
    }

    public static UltSlotProbe? TryCreate(string leagueRoot, (int X, int Y, int Width, int Height) rect)
    {
        var scale = HudScale(leagueRoot);
        if (scale is not { } s || Math.Abs(s - CalibratedScale) > 0.01)
        {
            Log.Info($"Ult cooldown sampling off: HUD scale {(scale?.ToString("0.00", CultureInfo.InvariantCulture) ?? "unknown")} is not the calibrated {CalibratedScale:0.00} - ults come from R presses alone");
            return null;
        }
        var k = rect.Height / 1440.0;
        var size = (int)Math.Round(52 * k);
        try
        {
            return new UltSlotProbe(
                rect.X + rect.Width / 2 - size / 2,
                rect.Y + rect.Height - (int)Math.Round(105 * k) - size / 2,
                size);
        }
        catch (Exception ex)
        {
            Log.Warn($"Ult cooldown sampling unavailable: {ex.Message}");
            return null;
        }
    }

    /// GlobalScale from game.cfg's [HUD] section; the game rewrites the file
    /// when the setting changes, so it is current by the time a game runs.
    private static double? HudScale(string leagueRoot)
    {
        try
        {
            var cfg = Path.Combine(leagueRoot, "Config", "game.cfg");
            if (leagueRoot.Length == 0 || !File.Exists(cfg)) return null;
            foreach (var line in File.ReadLines(cfg))
            {
                if (line.StartsWith("GlobalScale=", StringComparison.Ordinal)
                    && double.TryParse(line["GlobalScale=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// Mean brightness (0-255) of the slot as the screen shows it now.
    public int? Sample()
    {
        if (!BitBlt(_memory, 0, 0, _size, _size, _screen, _x, _y, SrcCopy)) return null;
        long sum = 0;
        var pixels = _size * _size;
        for (var i = 0; i < pixels; i++)
        {
            var bgra = Marshal.ReadInt32(_bits, i * 4);
            sum += (bgra & 0xFF) + ((bgra >> 8) & 0xFF) + ((bgra >> 16) & 0xFF);
        }
        return (int)(sum / (3L * pixels));
    }

    public void Dispose()
    {
        SelectObject(_memory, _previous);
        DeleteObject(_bitmap);
        DeleteDC(_memory);
        ReleaseDC(0, _screen);
    }

    private const uint SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
}
