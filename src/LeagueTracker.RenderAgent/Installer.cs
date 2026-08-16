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
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LeagueTrackerAgent";
    private const string DisplayName = "LeagueTracker Agent";

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
        RegisterAsApp();
        Log.Info($"Installed: runs at logon ({RunKey}\\{ValueName}), listed in Settings > Apps, Start Menu shortcut");

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

    /// What makes Windows treat the folder as an installed app: a Start Menu
    /// shortcut (Start search finds it, with the bolt icon) and an entry in
    /// Settings > Apps with version and a working Uninstall - all per-user,
    /// no admin, no MSI. Idempotent: every --install refreshes them, so a
    /// self-updated agent's version stays right after its next setup.
    /// Present when Setup.exe (Inno) put the files here: it owns the Start
    /// Menu and Settings > Apps entries then, and its uninstaller calls
    /// --uninstall --quiet.
    private static bool InstalledBySetup => File.Exists(Path.Combine(AppContext.BaseDirectory, "setup.installed"));
    private static bool Quiet => Environment.GetCommandLineArgs().Contains("--quiet");

    private static void RegisterAsApp()
    {
        if (InstalledBySetup) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", DisplayName);
            key.SetValue("DisplayVersion", AgentConfig.Version);
            key.SetValue("Publisher", "LeagueTracker");
            key.SetValue("DisplayIcon", ExePath);
            key.SetValue("InstallLocation", AppContext.BaseDirectory.TrimEnd('\\'));
            key.SetValue("UninstallString", $"\"{ExePath}\" --uninstall");
            key.SetValue("ModifyPath", $"\"{ExePath}\" --setup");
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("EstimatedSize", (int)(new DirectoryInfo(AppContext.BaseDirectory).EnumerateFiles().Sum(f => f.Length) / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Warn($"Could not register in Settings > Apps: {ex.Message}"); }

        try
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = ((dynamic)shell).CreateShortcut(ShortcutPath);
            shortcut.TargetPath = ExePath;
            shortcut.Arguments = "--setup";
            shortcut.WorkingDirectory = AppContext.BaseDirectory;
            shortcut.IconLocation = ExePath + ",0";
            shortcut.Description = "LeagueTracker agent settings";
            shortcut.Save();
        }
        catch (Exception ex) { Log.Warn($"Could not create the Start Menu shortcut: {ex.Message}"); }
    }

    private static void UnregisterAsApp()
    {
        if (InstalledBySetup) return;
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { /* best-effort */ }
        try { File.Delete(ShortcutPath); } catch { /* best-effort */ }
    }

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), DisplayName + ".lnk");

    public static int Uninstall()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        UnregisterAsApp();
        // Ask any running copy to stop the polite way (finish/postpone first).
        var running = OtherInstance(ExePath) is not null;
        if (running) File.WriteAllText(RenderAgent.StopSentinelPath, "uninstall");
        Log.Info("Uninstalled: no longer runs at logon" + (running ? "; the running agent is stopping" : ""));
        if (running && Quiet)
        {
            // Setup's uninstaller is about to delete the files: give the agent
            // its moment to stop first (its loops honour the sentinel).
            for (var i = 0; i < 60 && OtherInstance(ExePath) is not null; i++) Thread.Sleep(1000);
        }
        if (Quiet) return 0;
        MessageBox.Show("LeagueTracker agent removed from startup" + (running ? " - the running agent stops as soon as it is idle." : ".") +
                        "\n\nRecordings and settings stay where they are.", "LeagueTracker agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}
