using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LeagueTracker.RenderAgent;

/// The one piece of UI: an icon by the clock that says what the agent is
/// doing and carries the off switch. Lives on its own STA thread with a
/// WinForms message pump; the agent's async loops never touch it directly -
/// they update AgentStatus and the tray redraws itself.
public sealed class AgentTray : IDisposable
{
    private readonly AgentConfig _config;
    private readonly Action _quit;
    private readonly Func<Task> _checkForUpdates;
    private readonly Thread _thread;
    private NotifyIcon? _icon;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _pauseItem;
    private SynchronizationContext? _ui;
    private readonly Dictionary<string, Icon> _icons = [];

    public AgentTray(AgentConfig config, Action quit, Func<Task> checkForUpdates)
    {
        _config = config;
        _quit = quit;
        _checkForUpdates = checkForUpdates;
        _thread = new Thread(Pump) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        AgentStatus.Changed += () => _ui?.Post(_ => Refresh(), null);
    }

    private void Pump()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"LeagueTracker agent {AgentConfig.Version} · {_config.Role}") { Enabled = false });
        _statusItem = new ToolStripMenuItem("starting") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => { RenderAgent.SetPaused(!RenderAgent.Paused); Refresh(); });
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Open tracker", null, (_, _) => Open(_config.ServerUrls.FirstOrDefault() ?? "")));
        menu.Items.Add(new ToolStripMenuItem("Open recordings folder", null, (_, _) => Open(RecordingsDir())));
        menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => Open(Path.Combine(AppContext.BaseDirectory, "agent.log"))));
        menu.Items.Add(new ToolStripMenuItem("Check for updates", null, async (_, _) =>
        {
            try { await _checkForUpdates(); }
            catch (Exception ex) { Log.Warn($"Update check failed: {ex.Message}"); }
            _icon?.ShowBalloonTip(3000, "LeagueTracker agent", AgentStatus.Current.State is "updating" ? "Update staged - restarting" : $"Up to date ({AgentConfig.Version})", ToolTipIcon.Info);
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => _quit()));

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => Open(_config.ServerUrls.FirstOrDefault() ?? "");
        Refresh();
        Application.Run();
    }

    private void Refresh()
    {
        if (_icon is null || _statusItem is null || _pauseItem is null) return;
        var (state, detail) = AgentStatus.Current;
        var paused = RenderAgent.Paused;
        var line = paused && state is "idle" or "paused" ? "Paused - not recording or rendering" : Describe(state, detail);
        _statusItem.Text = line;
        _pauseItem.Text = paused ? "Resume" : "Pause (stop recording/rendering)";
        _icon.Text = Truncate($"LeagueTracker agent · {line}", 127);
        _icon.Icon = IconFor(paused ? "paused" : state is "starting" or "waiting" ? "waiting" : AgentStatus.LastError is not null && state is "idle" ? "warn" : state is "idle" ? "idle" : "busy");
    }

    private static string Describe(string state, string? detail) => state switch
    {
        "idle" => "Idle - watching for games",
        "recording" => $"Recording {detail}",
        "finalizing" => $"Finishing {detail}",
        "uploading" => $"Uploading {detail}",
        "rendering" => $"Rendering clips {detail}",
        "updating" => $"Updating to {detail}",
        "waiting" => detail ?? "Waiting for the tracker",
        _ => detail is { Length: > 0 } ? $"{state} {detail}" : state,
    };

    private Icon IconFor(string kind)
    {
        if (_icons.TryGetValue(kind, out var cached)) return cached;
        var color = kind switch
        {
            "busy" => Color.FromArgb(240, 85, 106),      // recording red
            "paused" => Color.FromArgb(120, 120, 130),
            "waiting" => Color.FromArgb(230, 180, 60),
            "warn" => Color.FromArgb(230, 140, 40),
            _ => Color.FromArgb(63, 185, 80),
        };
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(200, 20, 20, 25), 2);
            g.DrawEllipse(pen, 3, 3, 26, 26);
            if (kind is "paused")
            {
                using var bar = new SolidBrush(Color.White);
                g.FillRectangle(bar, 10, 9, 4, 14);
                g.FillRectangle(bar, 18, 9, 4, 14);
            }
        }
        var icon = Icon.FromHandle(bitmap.GetHicon());
        _icons[kind] = icon;
        return icon;
    }

    private string RecordingsDir() => _config.RecordingsDir is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LeagueTracker");

    private static void Open(string target)
    {
        if (target is not { Length: > 0 }) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {target}: {ex.Message}"); }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        _ui?.Post(_ =>
        {
            if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); }
            Application.ExitThread();
        }, null);
    }
}
