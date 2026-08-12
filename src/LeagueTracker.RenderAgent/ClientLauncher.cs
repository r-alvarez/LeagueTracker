using System.Diagnostics;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

/// Starts the League client through Riot's own launcher. Vanguard denies
/// direct launches of League binaries, but RiotClientServices IS the blessed
/// path - the exact thing the desktop shortcut runs. With "stay signed in"
/// ticked in the Riot client it comes up logged in with nobody at the
/// keyboard, which is the whole point: a machine woken for queued renders
/// has no one around to click an icon.
public static class ClientLauncher
{
    /// Riot's install registry, written by their installer.
    private static string InstallsJson => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Riot Games", "RiotClientInstalls.json");

    /// Full path to RiotClientServices.exe, or null when no install is found.
    public static string? Resolve(string leagueRoot)
    {
        try
        {
            if (File.Exists(InstallsJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(InstallsJson));
                foreach (var key in (string[])["rc_default", "rc_live", "rc_beta"])
                {
                    if (doc.RootElement.TryGetProperty(key, out var p)
                        && p.GetString() is { Length: > 0 } path && File.Exists(path))
                    {
                        return path;
                    }
                }
            }
        }
        catch
        {
            // Malformed installs file - fall through to the layout guess.
        }

        // The installer's fixed layout: League in "<root>\League of Legends",
        // the launcher in "<root>\Riot Client".
        var sibling = Path.Combine(
            Path.GetDirectoryName(leagueRoot.TrimEnd('\\', '/')) ?? "",
            "Riot Client", "RiotClientServices.exe");
        return File.Exists(sibling) ? sibling : null;
    }

    public static bool Launch(string riotClientServices)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(
                riotClientServices, "--launch-product=league_of_legends --launch-patchline=live"));
            return p is not null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Launching the League client failed: {ex.Message}");
            return false;
        }
    }
}
