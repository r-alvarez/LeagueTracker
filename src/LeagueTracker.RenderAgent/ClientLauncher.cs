using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
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

    /// The Riot hub's own local API - lockfile scheme identical to the LCU's.
    /// RiotClientServices ignores --launch-product entirely (observed
    /// 2026-08-12 on cold start AND with the hub already open) and only ever
    /// lands at the hub's Play button; this endpoint IS that Play button,
    /// minus the mouse (verified live: 200, LeagueClientUx came up). The API
    /// is the launch mechanism - the exe merely brings the hub up. False
    /// when the hub isn't running or the call fails. `quiet` mutes the
    /// failure warning for callers probing a hub that is still starting.
    public static async Task<bool> PressPlayAsync(CancellationToken ct, bool quiet = false)
    {
        var lockfile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Riot Games", "Riot Client", "Config", "lockfile");
        if (!File.Exists(lockfile)) return false;
        try
        {
            // name:pid:port:token:protocol - shared read; the hub keeps it open.
            using var stream = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var parts = new StreamReader(stream).ReadToEnd().Split(':');
            if (parts.Length < 5) return false;

            using var http = new HttpClient(new HttpClientHandler
            {
                // Self-signed local cert, same as the LCU.
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            });
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{parts[3]}")));
            using var resp = await http.PostAsync(
                $"https://127.0.0.1:{parts[2]}/product-launcher/v1/products/league_of_legends/patchlines/live",
                null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A stale lockfile from a crashed hub refuses the connection -
            // that's "not running", not an error worth more than a warning.
            if (!quiet) Log.Warn($"Pressing Play via the Riot Client API failed: {ex.Message}");
            return false;
        }
    }
}
