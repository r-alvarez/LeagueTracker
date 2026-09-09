using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LeagueTracker.RenderAgent.Review;

public static class ReviewApp
{
    public static string InstallId => RecordingLibrary.IdFor(AppContext.BaseDirectory.ToUpperInvariant());
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueTracker", "review", InstallId);
    private static string PipeName => "LeagueTracker.Review." + InstallId;
    private static string PidFile => Path.Combine(DataRoot, "viewer.pid");

    public static void Launch(bool last = false)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--review");
        if (last) start.ArgumentList.Add("--last");
        Process.Start(start)?.Dispose();
    }

    public static bool IsViewer(Process process)
    {
        try { return File.ReadAllText(PidFile) == $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}"; }
        catch { return false; }
    }

    public static int Run(AgentConfig config, bool last)
    {
        Directory.CreateDirectory(DataRoot);
        using var singleton = new Mutex(false, @"Local\" + PipeName);
        var owns = false;
        try
        {
            try { owns = singleton.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
            if (!owns)
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(3000);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.WriteLine(last ? "last" : "show");
                return 0;
            }
            using var self = Process.GetCurrentProcess();
            File.WriteAllText(PidFile, $"{self.Id}:{self.StartTime.ToUniversalTime().Ticks}");
            Exception? error = null;
            var ui = new Thread(() =>
            {
                try
                {
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    Application.EnableVisualStyles();
                    using var form = new ReviewForm(config, last, PipeName);
                    Application.Run(form);
                }
                catch (Exception ex) { error = ex; }
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ui.Join();
            if (error is not null) { Log.Error($"Review window: {error.Message}"); return 1; }
            return 0;
        }
        catch (Exception ex) { Log.Warn($"Could not open review: {ex.Message}"); return 1; }
        finally
        {
            if (owns) { try { File.Delete(PidFile); } catch (IOException) { } singleton.ReleaseMutex(); }
        }
    }

    internal static string ExtractAssets()
    {
        var assembly = typeof(ReviewApp).Assembly;
        using var resource = assembly.GetManifestResourceStream("review-ui.zip")
            ?? throw new FileNotFoundException("The review UI is missing. Run deploy/build-review-ui.ps1 and rebuild the agent.");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(resource))[..20];
        var parent = Path.Combine(DataRoot, "ui");
        var root = Path.Combine(parent, hash);
        if (File.Exists(Path.Combine(root, "desktop.html"))) return root;
        Directory.CreateDirectory(root);
        resource.Position = 0;
        using var zip = new ZipArchive(resource);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue;
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid UI archive entry.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
        if (!File.Exists(Path.Combine(root, "desktop.html"))) throw new IOException("Invalid review UI archive.");
        // Only generated, content-addressed UI directories. User data lives elsewhere.
        foreach (var old in new DirectoryInfo(parent).GetDirectories().Where(d => d.Name != hash))
            if (System.Text.RegularExpressions.Regex.IsMatch(old.Name, "^[a-f0-9]{20}$") && (old.Attributes & FileAttributes.ReparsePoint) == 0)
                try { old.Delete(true); } catch (IOException) { }
        return root;
    }
}

internal sealed class ReviewForm : Form
{
    private readonly AgentConfig _config;
    private readonly RecordingLibrary _library;
    private readonly ReviewApi _api;
    private readonly ReviewMediaServer _media;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _gameTimer = new() { Interval = 1500 };
    private readonly SemaphoreSlim _bridgeLimit = new(8);
    private bool _last;
    private bool _checkingGame;
    private bool _closing;
    private bool _disposed;
    private bool _initializing;
    private bool _listening;
    private bool _mediaStarted;
    private readonly Label _preparation = new() { Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, ForeColor = System.Drawing.Color.White };
    private readonly string _pipe;
    private readonly string? _leagueRoot;
    private readonly object _playbackGate = new();
    private FileStream? _playbackLease;

    public ReviewForm(AgentConfig config, bool last, string pipe)
    {
        _config = config;
        _last = last;
        _pipe = pipe;
        _leagueRoot = config.LeaguePath.Length > 0 ? config.LeaguePath : DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "Riot Games", "League of Legends"))
            .FirstOrDefault(Directory.Exists);
        _library = new RecordingLibrary(RecordingLibrary.RootFor(config));
        _api = new ReviewApi(config, Path.Combine(ReviewApp.DataRoot, "analysis"));
        _media = new ReviewMediaServer(_library, _api, ReviewApp.DataRoot);
        Text = "LeagueTracker · Gameplay review";
        Size = new System.Drawing.Size(1280, 860);
        MinimumSize = new System.Drawing.Size(860, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(11, 14, 21);
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Controls.Add(_view);
        Shown += async (_, _) => await InitializeAsync();
        _gameTimer.Tick += async (_, _) => await CheckGameAsync();
        FormClosing += (_, _) => { _closing = true; _lifetime.Cancel(); _gameTimer.Stop(); };
    }

    private async Task InitializeAsync()
    {
        if (_initializing || _closing) return;
        _initializing = true;
        try
        {
            await CheckGameAsync();
            if (_closing) return;
            if (!_listening) { _listening = true; _ = ListenAsync(); }
            _gameTimer.Start();
            if (!WebViewRuntime.IsInstalled())
            {
                Controls.Clear();
                _preparation.Text = "Preparing gameplay review…\nThis one-time setup may take a few minutes.";
                Controls.Add(_preparation);
                var result = await WebViewRuntime.EnsureAsync(_lifetime.Token);
                if (_closing) return;
                if (!result.Available) { ShowFailure(result.Error!, true); return; }
            }
            Controls.Clear();
            Controls.Add(_view);
            if (!_mediaStarted) { await _media.StartAsync(_lifetime.Token); _mediaStarted = true; }
            var assets = ReviewApp.ExtractAssets();
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disk-cache-size=33554432" };
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ReviewApp.DataRoot, "browser"), options);
            await _view.EnsureCoreWebView2Async(environment);
            var core = _view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping(new Uri(ReviewMediaServer.AppOrigin).Host, assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (!TrustedPage(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += (_, _) => Close();
            core.Navigate(ReviewMediaServer.AppOrigin + "/desktop.html");
            await CheckGameAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFailure("Gameplay review could not start after setup. Restart LeagueTracker and try again. Recording can continue.", true);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        { if (!_closing) ShowFailure($"Could not open gameplay review. {ex.Message}"); }
        finally { _initializing = false; }
    }

    private void ShowFailure(string text, bool runtime = false)
    {
        Controls.Clear();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32), FlowDirection = FlowDirection.TopDown };
        panel.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new System.Drawing.Size(750, 0), ForeColor = System.Drawing.Color.White });
        if (runtime)
        {
            var retry = new Button { Text = "Retry setup", AutoSize = true };
            retry.Click += async (_, _) => await InitializeAsync();
            panel.Controls.Add(retry);
        }
        Controls.Add(panel);
    }

    internal static bool TrustedPage(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var value)
        && value.GetLeftPart(UriPartial.Authority) == ReviewMediaServer.AppOrigin && value.AbsolutePath == "/desktop.html";
    internal static bool GamePhaseBlocksReview(string? phase) => phase is "ChampSelect" or "GameStart" or "InProgress" or "Reconnect";

    private async Task CheckGameAsync()
    {
        if (_checkingGame || _closing) return;
        _checkingGame = true;
        try
        {
            var game = Process.GetProcessesByName("League of Legends");
            var running = game.Length > 0;
            foreach (var p in game) p.Dispose();
            if (running || RenderAgent.StopRequested) { Close(); return; }
            if (_leagueRoot is not null)
            {
                using var lcu = LcuClient.TryConnect(_leagueRoot);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(1000);
                if (lcu is not null && GamePhaseBlocksReview(await lcu.GetGameflowPhaseAsync(timeout.Token))) Close();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or HttpRequestException) { }
        finally { _checkingGame = false; }
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_lifetime.Token);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(_lifetime.Token);
                if (line is "show" or "last")
                {
                    _last |= line == "last";
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Activate();
                    _view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { activation = line }));
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { if (_lifetime.IsCancellationRequested) break; }
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!TrustedPage(e.Source) || !TrustedPage(_view.Source?.AbsoluteUri ?? "") || _closing) return;
        if (e.WebMessageAsJson.Length > 16384) return;
        string? id = null;
        var entered = false;
        try
        {
            var message = JsonNode.Parse(e.WebMessageAsJson)!;
            id = message["id"]?.GetValue<string>();
            if (id is null || id.Length > 64) return;
            entered = _bridgeLimit.Wait(0);
            if (!entered) throw new InvalidOperationException("The review window is busy. Try again shortly.");
            var operation = message["operation"]?.GetValue<string>() ?? "";
            var argument = message["argument"] as JsonObject ?? new JsonObject();
            var result = await Task.Run(() => DispatchAsync(operation, argument, _lifetime.Token), _lifetime.Token);
            if (!_closing) _view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closing && id is not null)
                _view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, error = ex is UnauthorizedAccessException or ArgumentException or IOException or InvalidOperationException ? ex.Message : "The review request failed. Please try again." }));
        }
        finally { if (entered) _bridgeLimit.Release(); }
    }

    private async Task<object?> DispatchAsync(string operation, JsonObject arg, CancellationToken ct)
    {
        string Text(string key) => arg[key]?.GetValue<string>() ?? "";
        bool Flag(string key) => arg[key]?.GetValue<bool>() == true;
        switch (operation)
        {
            case "bootstrap": return new { artPrefix = _media.ArtPrefix, last = _last, version = AgentConfig.Version };
            case "library": return new { recordings = _library.List(), settings = _library.Settings(_config), freeGb = RecordingLibrary.FreeGb(_library.Root) };
            case "accounts": return await _api.DiscoverAsync(Flag("refresh"), ct);
            case "pin": _library.Pin(Text("id"), Flag("pinned")); return true;
            case "delete": _library.Delete(Text("id"), Flag("confirm")); return true;
            case "settings": _library.SaveSettings(arg.Deserialize<LibrarySettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!); return true;
            case "localVod": return LocalVod(_library.Find(Text("id")), false);
            case "localApm": return _library.Apm(_library.Find(Text("id")));
            case "releasePlayback": lock (_playbackGate) { _playbackLease?.Dispose(); _playbackLease = null; } return true;
            case "get":
            {
                var account = Text("account");
                var path = Text("path");
                _api.UriFor(account, path); // Validate the account and operation before any local override.
                if (path.EndsWith("/vod/status") && Text("recording") is { Length: > 0 } localId)
                {
                    var local = _library.Find(localId);
                    if (local.Available && path == $"/matches/{local.MatchId}/vod/status")
                        return new CachedReply(200, LocalVod(local).ToJsonString(), DateTime.UtcNow);
                }
                var reply = await _api.GetAsync(account, path, Flag("refresh"), ct);
                if (reply.Status != 200) return reply;
                var node = JsonNode.Parse(reply.Body);
                if (path.EndsWith("/vod/status"))
                {
                    if (node is JsonObject vod)
                    {
                        vod["playbackUrl"] = _media.Remote(account, path[..^7]);
                        vod["posterUrl"] = _media.Remote(account, path[..^7] + "/thumb");
                    }
                }
                else if (path.EndsWith("/fullgame/status") && node is JsonObject full)
                    full["playbackUrl"] = _media.Remote(account, path[..^7]);
                else if (path.EndsWith("/clips") && node is JsonArray clips)
                    foreach (var clip in clips.OfType<JsonObject>()) clip["url"] = _media.Remote(account, path + "/" + clip["index"]!.GetValue<int>());
                return reply with { Body = node?.ToJsonString() ?? "null" };
            }
            case "openWebsite":
            {
                var account = _api.Account(Text("account"));
                var tail = Text("match");
                if (tail.Length > 0 && !_apiPathMatch(tail)) throw new ArgumentException("Invalid match.");
                var url = $"{account.Server}/{Uri.EscapeDataString(account.Region)}/{Uri.EscapeDataString(account.Slug)}/" + (tail.Length > 0 ? "matches/" + tail : "");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            case "openYouTube":
            {
                if (!Uri.TryCreate(Text("url"), UriKind.Absolute, out var uri) || uri.Scheme != "https"
                    || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Host is not ("www.youtube.com" or "youtube.com" or "youtu.be")) throw new ArgumentException("Invalid YouTube link.");
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            default: throw new ArgumentException("Unknown review operation.");
        }
    }

    private static bool _apiPathMatch(string id) => ReviewApi.IsReviewPath("/matches/" + id) && !id.Contains('/');
    private JsonObject LocalVod(LocalRecording recording, bool includeApm = true)
    {
        lock (_playbackGate)
        {
            if (_closing) throw new OperationCanceledException();
            var lease = _library.OpenVideo(recording.Id);
            _playbackLease?.Dispose();
            _playbackLease = lease; // Keep paused playback protected between HTTP range requests.
        }
        return new()
        {
            ["exists"] = recording.Available,
            ["sizeMb"] = recording.SizeBytes / 1024d / 1024,
            ["youtubeUrl"] = null,
            ["meta"] = _library.MetadataFor(recording),
            ["apm"] = includeApm ? _library.Apm(recording) : null,
            ["playbackUrl"] = _media.Local(recording.Id),
            ["posterUrl"] = _media.Thumbnail(recording.Id),
            ["local"] = true,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _closing = true;
            lock (_playbackGate) { _playbackLease?.Dispose(); _playbackLease = null; }
            _lifetime.Cancel();
            _gameTimer.Dispose();
            _view.Dispose();
            _preparation.Dispose();
            // No UI continuations are required by the media server.
            Task.Run(async () => await _media.DisposeAsync()).GetAwaiter().GetResult();
            _api.Dispose();
        }
        base.Dispose(disposing);
    }
}
