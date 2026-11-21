using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

/// Covers the League client's post-game LP readout for a player who switched
/// on Hide LP on the tracker's Data page - the site already kept LP off its
/// own pages, but the client is where the number actually lands. A plain
/// window laid over the client: nothing is injected or modified, so there is
/// nothing for Vanguard to object to. The client's gameflow phase says when
/// the post-game screens are up, which is all the OCR in tools like HideLP
/// was ever for.
public sealed class LpCover(string leagueRoot, IReadOnlyList<TrackerClient> servers)
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    // The toggle lives on the site; a flip reaches the PC within this.
    private static readonly TimeSpan SettingEvery = TimeSpan.FromMinutes(5);
    private static readonly HashSet<long> LpQueues = [420, 440];

    private LcuClient? _lcu;
    private bool _hideLp;
    private string? _settingPuuid;
    private DateTime _settingReadUtc = DateTime.MinValue;

    public async Task RunAsync(CancellationToken ct)
    {
        using var window = new LpCoverWindow();
        while (!ct.IsCancellationRequested && !RenderAgent.StopRequested)
        {
            try
            {
                window.Cover(await PhaseToCoverAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"LP cover pass failed: {ex.Message}");
                window.Cover(null);
            }
            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static bool ShouldCover(string phase, long queueId, bool hideLp) =>
        hideLp
        && phase is ("PreEndOfGame" or "EndOfGame")
        // An unknown queue is covered: a flash of LP is the one thing this exists to prevent.
        && (queueId < 0 || LpQueues.Contains(queueId));

    private async Task<string?> PhaseToCoverAsync(CancellationToken ct)
    {
        _lcu ??= LcuClient.TryConnect(leagueRoot);
        if (_lcu is null) return null;
        if (await _lcu.GetGameflowPhaseAsync(ct) is not { } phase)
        {
            // Closed or restarting: the next lockfile carries a new port and token.
            _lcu.Dispose();
            _lcu = null;
            return null;
        }

        await RefreshSettingAsync(_lcu, ct);
        if (!_hideLp || phase is not ("PreEndOfGame" or "EndOfGame")) return null;
        var queueId = await _lcu.GetGameSessionAsync(ct) is { } session ? session.QueueId : -1;
        return ShouldCover(phase, queueId, _hideLp) ? phase : null;
    }

    /// Per signed-in account: a shared PC follows whoever is logged in, and
    /// an account the trackers don't know is never covered.
    private async Task RefreshSettingAsync(LcuClient lcu, CancellationToken ct)
    {
        if (await lcu.GetCurrentPuuidAsync(ct) is not { Length: > 0 } puuid) return;
        if (puuid == _settingPuuid && DateTime.UtcNow - _settingReadUtc < SettingEvery) return;

        var known = new List<TrackerAccount>();
        foreach (var server in servers)
        {
            if (await server.GetAccountsAsync(ct) is { } accounts) known.AddRange(accounts);
        }
        _settingReadUtc = DateTime.UtcNow;
        // Every tracker unreachable: keep the last answer rather than flash LP over a NAS blip.
        if (known is not { Count: > 0 } && puuid == _settingPuuid) return;

        var hide = known.Exists(a => a.HideLp && a.Puuid == puuid);
        if (hide != _hideLp || puuid != _settingPuuid)
        {
            Log.Info(hide ? "Hide LP is on for the signed-in account - post-game LP will be covered in the client" : "Hide LP is off for the signed-in account");
        }
        _settingPuuid = puuid;
        _hideLp = hide;
    }
}

/// Where a cover sits, as fractions of the client window, so it follows the
/// client across window sizes and monitors.
internal sealed record CoverRect(double X, double Y, double Width, double Height)
{
    public Rectangle Within((int X, int Y, int Width, int Height) client) => new(
        client.X + (int)Math.Round(client.Width * X),
        client.Y + (int)Math.Round(client.Height * Y),
        (int)Math.Round(client.Width * Width),
        (int)Math.Round(client.Height * Height));

    public static CoverRect Of(Rectangle bounds, (int X, int Y, int Width, int Height) client) => new(
        (double)(bounds.X - client.X) / client.Width,
        (double)(bounds.Y - client.Y) / client.Height,
        (double)bounds.Width / client.Width,
        (double)bounds.Height / client.Height);
}

/// The cover itself, on its own STA thread and message pump like the tray,
/// so the async loop only ever says "cover this phase" or "nothing".
internal sealed class LpCoverWindow : IDisposable
{
    private readonly ManualResetEventSlim _ready = new(false);
    private SynchronizationContext? _ui;
    private CoverForm? _form;

    public LpCoverWindow()
    {
        var thread = new Thread(Pump) { IsBackground = true, Name = "lp-cover" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Pump()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);
        _form = new CoverForm();
        _ready.Set();
        Application.Run();
    }

    public void Cover(string? phase) => _ui?.Post(_ => _form?.Cover(phase), null);

    public void Dispose() => _ui?.Post(_ =>
    {
        _form?.Dispose();
        Application.ExitThread();
    }, null);
}

/// Drag to move, drag the bottom-right corner to resize. Where it ends up is
/// remembered per phase in lp-cover.json next to the exe. The defaults are a
/// first guess at the current client layout - Riot moves things, and one
/// drag fixes it for good.
internal sealed class CoverForm : Form
{
    private const int Grip = 14;
    private static string LayoutPath => Path.Combine(AppContext.BaseDirectory, "lp-cover.json");

    private static readonly Dictionary<string, CoverRect> Defaults = new()
    {
        ["PreEndOfGame"] = new(0.35, 0.62, 0.30, 0.18),
        ["EndOfGame"] = new(0.03, 0.10, 0.30, 0.14),
    };

    private readonly Dictionary<string, CoverRect> _layout = LoadLayout();
    private readonly System.Windows.Forms.Timer _follow = new() { Interval = 250 };
    private string? _phase;
    private bool _dragging;

    public CoverForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(1, 10, 19);  // the client's own background, so it reads as part of it
        ForeColor = Color.FromArgb(120, 120, 110);
        MinimumSize = new Size(40, 20);
        DoubleBuffered = true;
        _follow.Tick += (_, _) => Follow();
        ResizeBegin += (_, _) => _dragging = true;
        ResizeEnd += (_, _) => { _dragging = false; RememberPlacement(); };
        _ = Handle;  // created now, on the pump thread, so Post from the loop always has a target
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    public void Cover(string? phase)
    {
        if (phase == _phase) return;
        _phase = phase;
        if (phase is null)
        {
            _follow.Stop();
            Hide();
            return;
        }
        _follow.Start();
        Follow();
    }

    /// Hidden whenever the client is not what the player is looking at - a
    /// topmost box must not sit over their browser.
    private void Follow()
    {
        if (_phase is null || ClientWindow() is not { } client || !IsInFront(client)
            || GameWindow.ClientRectOf(client) is not { Width: > 0, Height: > 0 } rect)
        {
            if (Visible) Hide();
            return;
        }
        if (!_dragging) Bounds = PlacementFor(_phase).Within(rect);
        if (!Visible) Show();
    }

    private CoverRect PlacementFor(string phase) =>
        _layout.TryGetValue(phase, out var saved) ? saved : Defaults[phase];

    private void RememberPlacement()
    {
        if (_phase is null || ClientWindow() is not { } client || GameWindow.ClientRectOf(client) is not { Width: > 0, Height: > 0 } rect) return;
        _layout[_phase] = CoverRect.Of(Bounds, rect);
        try
        {
            File.WriteAllText(LayoutPath, JsonSerializer.Serialize(_layout, new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"LP cover for {_phase} moved - remembered in lp-cover.json");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the LP cover position: {ex.Message}");
        }
    }

    private static Dictionary<string, CoverRect> LoadLayout()
    {
        try
        {
            if (File.Exists(LayoutPath) && JsonSerializer.Deserialize<Dictionary<string, CoverRect>>(File.ReadAllText(LayoutPath)) is { } saved)
            {
                return saved;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"lp-cover.json unreadable, using the default cover positions: {ex.Message}");
        }
        return [];
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics, "LP hidden", Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var grip = new SolidBrush(Color.FromArgb(60, 60, 55));
        e.Graphics.FillPolygon(grip, new[] { new Point(Width, Height - Grip), new Point(Width, Height), new Point(Width - Grip, Height) });
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    // Borderless, so Windows is told where the "title bar" and the resize
    // corner are: the corner resizes, everything else drags.
    protected override void WndProc(ref Message m)
    {
        const int WmNcHitTest = 0x84, HtClient = 1, HtCaption = 2, HtBottomRight = 17;
        base.WndProc(ref m);
        if (m.Msg is not WmNcHitTest || (int)m.Result is not HtClient) return;
        var at = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        m.Result = at.X >= Width - Grip && at.Y >= Height - Grip ? HtBottomRight : HtCaption;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _follow.Dispose();
        base.Dispose(disposing);
    }

    private static nint? ClientWindow()
    {
        var hwnd = FindWindowW("RCLIENT", null);
        return hwnd is 0 || IsIconic(hwnd) ? null : hwnd;
    }

    private bool IsInFront(nint client)
    {
        var foreground = GetForegroundWindow();
        if (foreground == client || foreground == Handle) return true;
        // The client's own popups (honor, ceremonies) are separate windows of the same process.
        GetWindowThreadProcessId(foreground, out var foregroundPid);
        GetWindowThreadProcessId(client, out var clientPid);
        return foregroundPid == clientPid;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
