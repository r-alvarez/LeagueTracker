using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

// Opens archived tracker replays straight from the browser. The match pages
// link to leaguereplay://<tracker-host>/<matchId>; Windows routes that here
// (after a one-time `--register`). Vanguard denies direct launches of the
// game binary, so the replay goes through the League client: download the
// .rofl into the client's Replays folder, ask it to scan, then watch.
// Silent on success - the replay window appearing is the feedback.

try
{
    if (args is ["--register"])
    {
        var exe = Environment.ProcessPath!;
        using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\leaguereplay");
        root.SetValue("", "URL:LeagueTracker Replay");
        root.SetValue("URL Protocol", "");
        using var command = root.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exe}\" \"%1\"");
        Native.Info($"leaguereplay:// links now open with:\n{exe}");
        return 0;
    }

    if (args is not [var raw] || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "leaguereplay")
    {
        Native.Error("Usage:\n  LeagueTracker.ReplayLauncher --register\n  LeagueTracker.ReplayLauncher leaguereplay://<tracker-host>/<matchId>");
        return 1;
    }

    var matchId = uri.AbsolutePath.Trim('/');
    var idParts = matchId.Split('_');
    if (idParts is not [{ Length: > 0 } platform, var rawGameId] || !long.TryParse(rawGameId, out var gameId)
        || !platform.All(char.IsLetterOrDigit))
    {
        Native.Error($"Bad match id in link: \"{matchId}\"");
        return 1;
    }

    if (FindLockfile() is not { } lockfile)
    {
        Native.Error("The League client isn't running.\n\nStart it (and log in), then click the link again - Vanguard only allows replays launched through the client.");
        return 1;
    }

    using var lcu = Lcu.Connect(lockfile);
    var replaysDir = await lcu.GetStringAsync("/lol-replays/v1/rofls/path");
    var rofl = Path.Combine(replaysDir, $"{platform}-{gameId}.rofl");

    if (!File.Exists(rofl))
    {
        using var http = new HttpClient();
        using var response = await GetWithFallbackAsync(http, uri.Authority, matchId);
        if (!response.IsSuccessStatusCode)
        {
            Native.Error($"Tracker returned {(int)response.StatusCode} downloading the replay for {matchId}.");
            return 1;
        }
        await using var file = File.Create(rofl);
        await response.Content.CopyToAsync(file);
    }

    await lcu.PostAsync("/lol-replays/v1/rofls/scan");
    await Task.Delay(TimeSpan.FromSeconds(2));
    await lcu.PostAsync($"/lol-replays/v1/rofls/{gameId}/watch", "{}");
    return 0;
}
catch (Exception ex)
{
    Native.Error(ex.Message);
    return 1;
}

static string? FindLockfile()
{
    foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
    {
        var lockfile = Path.Combine(drive.RootDirectory.FullName, "Riot Games", "League of Legends", "lockfile");
        if (File.Exists(lockfile)) return lockfile;
    }
    return null;
}

static async Task<HttpResponseMessage> GetWithFallbackAsync(HttpClient http, string authority, string matchId)
{
    var path = $"/api/matches/{matchId}/replay";
    try
    {
        return await http.GetAsync($"https://{authority}{path}");
    }
    catch (HttpRequestException)
    {
        return await http.GetAsync($"http://{authority}{path}");
    }
}

sealed class Lcu : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    private Lcu(int port, string token)
    {
        _http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{token}")));
        _base = $"https://127.0.0.1:{port}";
    }

    public static Lcu Connect(string lockfilePath)
    {
        // name:pid:port:token:protocol - shared read; the client keeps it open.
        using var stream = new FileStream(lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var parts = new StreamReader(stream).ReadToEnd().Split(':');
        return new Lcu(int.Parse(parts[2]), parts[3]);
    }

    public async Task<string> GetStringAsync(string path) =>
        JsonSerializer.Deserialize<string>(await _http.GetStringAsync(_base + path))
            ?? throw new InvalidOperationException($"unexpected reply from the client for {path}");

    public async Task PostAsync(string path, string? json = null)
    {
        using var content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_base + path, content);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static void Info(string text) => MessageBoxW(0, text, "LeagueTracker Replay", 0x40);
    public static void Error(string text) => MessageBoxW(0, text, "LeagueTracker Replay", 0x10);
}
