using System.Diagnostics;
using Microsoft.Win32;

namespace LeagueTracker.RenderAgent;

/// "Run at logon" without an installer: a per-user Run key, so no admin
/// rights, and the agent starts in the interactive session (capture needs a
/// real desktop). --install also starts it now and reports in a message box
/// - the exe has no console for a friend to read.
public static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LeagueTrackerAgent";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LeagueTracker.RenderAgent.exe");

    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// Another process running THIS exe (same path) - not a dev copy from
    /// bin/ next to a deployed agent, which shares the name and nothing else.
    public static Process? OtherInstance(string exe) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).FirstOrDefault(p =>
        {
            if (p.Id == Environment.ProcessId) return false;
            try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });

    /// The setup window alone (tray "Settings…"): saves and restarts the
    /// running agent, touches nothing else.
    public static int Setup(AgentConfig config)
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var setup = new SetupForm(config);
        if (setup.ShowDialog() != DialogResult.OK) return 1;
        if (OtherInstance(ExePath) is not null) AgentSupervisor.RestartRunningAgent();
        return 0;
    }

    public static int Install(AgentConfig config)
    {
        // The setup window first: --install is what a friend double-clicks,
        // and it must be able to start from a bare zip.
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using (var setup = new SetupForm(config))
        {
            if (setup.ShowDialog() != DialogResult.OK) return 1;
        }
        config = AgentConfig.Load();

        var problems = new List<string>();
        if (SetupForm.NeedsSetup(config)) problems.Add("ServerUrl in appsettings.json still points at localhost");
        if (RenderAgent.ResolveFfmpeg(config) is not { Length: > 0 }) problems.Add("ffmpeg not found (winget install Gyan.FFmpeg, or ffmpeg.exe next to the agent)");

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            key.SetValue(ValueName, $"\"{ExePath}\"");
        }
        Log.Info($"Installed: runs at logon ({RunKey}\\{ValueName})");

        // A running agent restarts so the new settings take: stop sentinel,
        // then a detached cmd relaunches once it has gone.
        var alreadyRunning = OtherInstance(ExePath) is not null;
        if (alreadyRunning) AgentSupervisor.RestartRunningAgent();
        else Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });

        var summary = $"LeagueTracker agent {AgentConfig.Version} installed - it now starts with Windows and is {(alreadyRunning ? "restarting with the new settings" : "starting now")}.\n\n" +
                      $"Role: {config.Role}\nTracker: {config.ServerUrl}\n\n" +
                      "Look for the icon in the tray next to the clock: right-click for pause/resume, the log, and quit.";
        if (problems is { Count: > 0 }) summary += "\n\nNeeds attention:\n - " + string.Join("\n - ", problems);
        MessageBox.Show(summary, "LeagueTracker agent", MessageBoxButtons.OK, problems is { Count: > 0 } ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        return problems is { Count: > 0 } ? 1 : 0;
    }

    public static int Uninstall()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        // Ask any running copy to stop the polite way (finish/postpone first).
        var running = OtherInstance(ExePath) is not null;
        if (running) File.WriteAllText(RenderAgent.StopSentinelPath, "uninstall");
        Log.Info("Uninstalled: no longer runs at logon" + (running ? "; the running agent is stopping" : ""));
        MessageBox.Show("LeagueTracker agent removed from startup" + (running ? " - the running agent stops as soon as it is idle." : ".") +
                        "\n\nRecordings and settings stay where they are.", "LeagueTracker agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}
