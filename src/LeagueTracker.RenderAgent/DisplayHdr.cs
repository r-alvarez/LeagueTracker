using System.Runtime.InteropServices;

namespace LeagueTracker.RenderAgent;

// Whether the display under the game window is running in HDR. Both
// capture engines read the DWM-composited desktop as 8-bit SDR; when the
// desktop is HDR that read is a clamp, not a conversion (Windows' SDR
// brightness boost, Auto HDR / RTX HDR highlights and the wide gamut all
// land above 1.0 and clip), so the recording comes out brighter and paler
// than the screen with blown highlights. Nothing downstream can undo it -
// the recorder can only say so.
internal static class DisplayHdr
{
    // True/false for the display holding the rect, null when Windows would
    // not say (pre-HDR OS, no matching path, RDP session).
    public static bool? IsOnFor((int X, int Y, int Width, int Height) rect)
    {
        try
        {
            var deviceName = DeviceNameOf(rect);
            if (deviceName is null) return null;
            foreach (var path in ActivePaths())
            {
                if (SourceDeviceName(path.SourceAdapter, path.SourceId) != deviceName) continue;
                return HdrActive(path.TargetAdapter, path.TargetId);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? DeviceNameOf((int X, int Y, int Width, int Height) rect)
    {
        var r = new NativeRect { Left = rect.X, Top = rect.Y, Right = rect.X + rect.Width, Bottom = rect.Y + rect.Height };
        var monitor = MonitorFromRect(ref r, MonitorDefaultToNearest);
        if (monitor == 0) return null;
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfoW(monitor, ref info) ? info.Device : null;
    }

    private static IEnumerable<(long SourceAdapter, uint SourceId, long TargetAdapter, uint TargetId)> ActivePaths()
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) yield break;
        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, 0) != 0) yield break;
        for (var i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            yield return (p.SourceAdapterId, p.SourceId, p.TargetAdapterId, p.TargetId);
        }
    }

    private static string? SourceDeviceName(long adapter, uint id)
    {
        var packet = new SourceDeviceNamePacket
        {
            Type = DeviceInfoGetSourceName,
            Size = Marshal.SizeOf<SourceDeviceNamePacket>(),
            AdapterId = adapter,
            Id = id,
        };
        return DisplayConfigGetDeviceInfo(ref packet) == 0 ? packet.ViewGdiDeviceName : null;
    }

    // Windows 11 24H2 tells HDR apart from the wide-colour desktop mode
    // (ACM), which stays 8-bit-capturable; the older query lumps both under
    // "advanced colour enabled" and is what pre-24H2 systems answer.
    private static bool? HdrActive(long adapter, uint id)
    {
        var v2 = new AdvancedColorInfo2Packet
        {
            Type = DeviceInfoGetAdvancedColorInfo2,
            Size = Marshal.SizeOf<AdvancedColorInfo2Packet>(),
            AdapterId = adapter,
            Id = id,
        };
        if (DisplayConfigGetDeviceInfo(ref v2) == 0) return v2.ActiveColorMode == ColorModeHdr;

        var v1 = new AdvancedColorInfoPacket
        {
            Type = DeviceInfoGetAdvancedColorInfo,
            Size = Marshal.SizeOf<AdvancedColorInfoPacket>(),
            AdapterId = adapter,
            Id = id,
        };
        if (DisplayConfigGetDeviceInfo(ref v1) != 0) return null;
        return (v1.Value & AdvancedColorEnabledBit) != 0;
    }

    private const uint QdcOnlyActivePaths = 0x2;
    private const uint MonitorDefaultToNearest = 2;
    private const int DeviceInfoGetSourceName = 1;
    private const int DeviceInfoGetAdvancedColorInfo = 9;
    private const int DeviceInfoGetAdvancedColorInfo2 = 15;
    private const uint AdvancedColorEnabledBit = 0x2;
    private const int ColorModeHdr = 2;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayConfigPathInfo[] paths, ref uint modeCount, [Out] DisplayConfigModeInfo[] modes, nint currentTopology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceNamePacket packet);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfoPacket packet);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo2Packet packet);

    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    // DISPLAYCONFIG_PATH_INFO flattened by hand: its nested unions don't
    // marshal as C# structs, and only the four ids are read. Pack = 4
    // because LUID is two DWORDs in C - the target block starts at byte 20,
    // where a C# long would round it up to 24 - and the device-info
    // packets are size-checked by Windows to the byte.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DisplayConfigPathInfo
    {
        public long SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoIdx;
        public uint SourceStatusFlags;
        public long TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public uint ScanLineOrdering;
        public uint TargetAvailable;
        public uint TargetStatusFlags;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo { }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct SourceDeviceNamePacket
    {
        public int Type;
        public int Size;
        public long AdapterId;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AdvancedColorInfoPacket
    {
        public int Type;
        public int Size;
        public long AdapterId;
        public uint Id;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AdvancedColorInfo2Packet
    {
        public int Type;
        public int Size;
        public long AdapterId;
        public uint Id;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public int ActiveColorMode;
    }
}
