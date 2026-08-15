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

    public static int Install(AgentConfig config)
    {
        var problems = new List<string>();
        if (config.ServerUrls is not { Length: > 0 } || config.ServerUrl.Contains("localhost")) problems.Add("ServerUrl in appsettings.json still points at localhost");
        if (RenderAgent.ResolveFfmpeg(config) is not { Length: > 0 }) problems.Add("ffmpeg not found (winget install Gyan.FFmpeg, or ffmpeg.exe next to the agent)");

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            key.SetValue(ValueName, $"\"{ExePath}\"");
        }
        Log.Info($"Installed: runs at logon ({RunKey}\\{ValueName})");

        var alreadyRunning = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath)).Length > 1;
        if (!alreadyRunning) Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });

        var summary = $"LeagueTracker agent {AgentConfig.Version} installed - it now starts with Windows and is {(alreadyRunning ? "already running" : "starting now")}.\n\n" +
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
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath)).Length > 1;
        if (running) File.WriteAllText(RenderAgent.StopSentinelPath, "uninstall");
        Log.Info("Uninstalled: no longer runs at logon" + (running ? "; the running agent is stopping" : ""));
        MessageBox.Show("LeagueTracker agent removed from startup" + (running ? " - the running agent stops as soon as it is idle." : ".") +
                        "\n\nRecordings and settings stay where they are.", "LeagueTracker agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}
