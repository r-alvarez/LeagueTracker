using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace LeagueTracker.RenderAgent;

/// Input telemetry for gameplay review: while a game is being recorded (and
/// only then), global low-level hooks log every key, click, wheel tick and
/// cursor position with a video-relative timestamp. This is what the review
/// UI's APM line and input overlay are computed from - the video shows what
/// happened on screen, this shows what the hands were doing.
///
/// The CSV schema (t_ms,event_type,input_name,value_a,value_b) deliberately
/// matches Ascent's events.csv.gz, so old Ascent telemetry and ours can be
/// read by the same review code. Written gzip-streamed next to the video.
public sealed class InputLogger : IDisposable
{
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _hookThread;
    private readonly Task _writer;
    private uint _hookThreadId;

    // Kept in fields: a GC'd hook delegate crashes the process on next input.
    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;

    public string Path { get; }

    /// When set, events are only logged while this window is foreground -
    /// alt-tabbing to type somewhere else mid-game must not land in the
    /// telemetry. Zero (tests) logs regardless of focus.
    private readonly nint _gameWindow;

    private InputLogger(string path, nint gameWindow)
    {
        Path = path;
        _gameWindow = gameWindow;
        _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
        // Low-level hooks need a thread that pumps messages; the callbacks
        // fire on it, so it must never block - callbacks only enqueue.
        _hookThread = new Thread(HookPump) { IsBackground = true, Name = "input-logger" };
        _hookThread.Start();
    }

    public static InputLogger? TryStart(string path, nint gameWindow = 0)
    {
        try
        {
            return new InputLogger(path, gameWindow);
        }
        catch (Exception ex)
        {
            Log.Warn($"Input logging unavailable: {ex.Message}");
            return null;
        }
    }

    private void HookPump()
    {
        _hookThreadId = GetCurrentThreadId();
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        var module = GetModuleHandleW(null);
        var keyboard = SetWindowsHookExW(WhKeyboardLl, _keyboardProc, module, 0);
        var mouse = SetWindowsHookExW(WhMouseLl, _mouseProc, module, 0);
        if (keyboard == 0 || mouse == 0)
        {
            Log.Warn("Input logging: could not install hooks");
        }

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        if (keyboard != 0) UnhookWindowsHookEx(keyboard);
        if (mouse != 0) UnhookWindowsHookEx(mouse);
    }

    private nint KeyboardHook(int code, nuint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var vk = (uint)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
            switch (wParam)
            {
                case WmKeydown or WmSyskeydown:
                    Enqueue("key_down", KeyName(vk), 0, 0);
                    break;
                case WmKeyup or WmSyskeyup:
                    Enqueue("key_up", KeyName(vk), 0, 0);
                    break;
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private nint MouseHook(int code, nuint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var x = Marshal.ReadInt32(lParam);       // MSLLHOOKSTRUCT.pt.x
            var y = Marshal.ReadInt32(lParam, 4);    // MSLLHOOKSTRUCT.pt.y
            switch (wParam)
            {
                case WmMousemove: Enqueue("cursor_pos", "", x, y); break;
                case WmLbuttondown: Enqueue("mouse_down", "left", x, y); break;
                case WmLbuttonup: Enqueue("mouse_up", "left", x, y); break;
                case WmRbuttondown: Enqueue("mouse_down", "right", x, y); break;
                case WmRbuttonup: Enqueue("mouse_up", "right", x, y); break;
                case WmMbuttondown: Enqueue("mouse_down", "middle", x, y); break;
                case WmMbuttonup: Enqueue("mouse_up", "middle", x, y); break;
                case WmMousewheel:
                    // High word of mouseData, signed multiples of 120.
                    var delta = (short)(Marshal.ReadInt32(lParam, 8) >> 16);
                    Enqueue("wheel", "", delta / 120, 0);
                    break;
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private void Enqueue(string type, string name, int a, int b)
    {
        if (_gameWindow != 0 && GetForegroundWindow() != _gameWindow) return;
        _pending.Enqueue($"{_clock.ElapsedMilliseconds},{type},{name},{a},{b}");
    }

    /// League binds letters, digits, F-keys and a few specials; everything
    /// else keeps a stable vk_NN name rather than being dropped.
    private static string KeyName(uint vk) => vk switch
    {
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        0x20 => "space", 0x09 => "tab", 0x0D => "enter", 0x1B => "esc",
        0x10 or 0xA0 or 0xA1 => "shift",
        0x11 or 0xA2 or 0xA3 => "ctrl",
        0x12 or 0xA4 or 0xA5 => "alt",
        _ => $"vk_{vk:X2}",
    };

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        await using var file = File.Create(Path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var writer = new StreamWriter(gzip, Encoding.ASCII);
        await writer.WriteLineAsync("t_ms,event_type,input_name,value_a,value_b");
        while (!ct.IsCancellationRequested || !_pending.IsEmpty)
        {
            while (_pending.TryDequeue(out var line)) await writer.WriteLineAsync(line);
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { }
        }
        while (_pending.TryDequeue(out var tail)) await writer.WriteLineAsync(tail);
    }

    public void Dispose()
    {
        // Order matters: stop the pump so no more events enqueue, then let
        // the writer drain the queue and close the gzip trailer.
        if (_hookThreadId != 0) PostThreadMessageW(_hookThreadId, WmQuit, 0, 0);
        _hookThread.Join(TimeSpan.FromSeconds(3));
        _cts.Cancel();
        try { _writer.Wait(TimeSpan.FromSeconds(5)); } catch { /* logged nothing worth losing the VOD over */ }
        _cts.Dispose();
    }

    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const nuint WmKeydown = 0x100;
    private const nuint WmKeyup = 0x101;
    private const nuint WmSyskeydown = 0x104;
    private const nuint WmSyskeyup = 0x105;
    private const nuint WmMousemove = 0x200;
    private const nuint WmLbuttondown = 0x201;
    private const nuint WmLbuttonup = 0x202;
    private const nuint WmRbuttondown = 0x204;
    private const nuint WmRbuttonup = 0x205;
    private const nuint WmMbuttondown = 0x207;
    private const nuint WmMbuttonup = 0x208;
    private const nuint WmMousewheel = 0x20A;
    private const uint WmQuit = 0x12;

    private delegate nint HookProc(int code, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookExW(int hookId, HookProc proc, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out NativeMsg msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMsg msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref NativeMsg msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? module);

    private struct NativeMsg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }
}
