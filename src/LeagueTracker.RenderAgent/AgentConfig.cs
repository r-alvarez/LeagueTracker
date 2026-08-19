using System.Reflection;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

public sealed class AgentConfig
{
    /// One or more tracker servers, comma-separated - one agent serves them all
    /// (two agent processes would fight over the game client).
    public string ServerUrl { get; set; } = "http://localhost:5170";

    public string[] ServerUrls => [.. ServerUrl.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(u => u.TrimEnd('/'))];
    public string LeaguePath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string AgentName { get; set; } = "";
    public int PollSeconds { get; set; } = 60;
    public int CaptureFramerate { get; set; } = 30;
    public int MaxWindowsPerJob { get; set; }

    /// Renders only start after this much keyboard/mouse idle time - the
    /// camera lock needs the game window focused, which can only be taken
    /// reliably (and politely) when nobody is using the PC.
    public int IdleSeconds { get; set; } = 120;

    /// Record live games (Ascent-style auto-VOD): capture the game window
    /// while the local player is in a real game.
    public bool RecordGames { get; set; } = true;

    /// Turn archived replays into clips for the trackers. Off = a recorder-only
    /// agent (a friend's gaming PC: record, publish, done); on without
    /// RecordGames = the dedicated render box that serves every tracker.
    public bool RenderReplays { get; set; } = true;

    public string Role => (RecordGames, RenderReplays) switch
    {
        (true, true) => "full",
        (true, false) => "recorder",
        (false, true) => "renderer",
        _ => "idle",
    };

    /// Where finished recordings (mp4 + sidecar json + thumbnail) land.
    /// Blank = <My Videos>\LeagueTracker.
    public string RecordingsDir { get; set; } = "";

    /// Where in-progress recording segments are written before the finished
    /// file moves to RecordingsDir. Blank = LocalAppData\LeagueTracker    /// recording-parts on the system drive. Keep this on an SSD: the encoder
    /// blocks on every write, and a stalling (spinning/SMR) recordings drive
    /// shows up as frozen frames in the VOD.
    public string RecordScratchDir { get; set; } = "";

    /// Live-game recording framerate. 60 reads better for mechanics review;
    /// 30 halves the file size.
    public int RecordFramerate { get; set; } = 60;

    /// NVENC constant-quality target (lower = better looking and bigger,
    /// roughly like x264 CRF). 26 lands near 1.5-3 GB per game at 1440p60.
    public int RecordQuality { get; set; } = 26;

    /// Log keyboard/mouse alongside each recording (events.csv.gz next to
    /// the mp4) - feeds the review UI's APM line and input overlay. Only
    /// active while a game recording is running.
    public bool RecordInputs { get; set; } = true;

    /// Non-blank = YouTube-ready recording names: "{prefix} - 22 Jul 2026 -
    /// Game 2" (numbered per day, by what's already in RecordingsDir).
    /// Blank = timestamp + match id names.
    public string RecordNamePrefix { get; set; } = "";

    /// Send finished VODs to the tracker that owns the match (the in-app
    /// review player). Off = recordings stay local only.
    public bool UploadVods { get; set; } = true;

    /// Send only the small review data (telemetry, metadata, thumbnail -
    /// megabytes) while the video itself lives elsewhere (YouTube). The
    /// match page then overlays markers/APM on the linked video. Moot when
    /// UploadVods is on (full uploads already include the sidecars).
    public bool UploadVodSidecars { get; set; } = true;

    /// Upload speed cap (Mbps) while a game is running. 0 = automatic: half
    /// of the line's measured idle upstream, never under 3 Mbps. Uploads
    /// never pause for a game - the customer expects game 1 on YouTube by the
    /// end of game 2 - they just leave the game its headroom.
    public double UploadInGameMbps { get; set; }

    /// Keep the local mp4 after it is safely on YouTube (uploaded, processed,
    /// linked on the tracker). Off = the idle sweep deletes it and keeps only
    /// the small sidecars; on = a debugging machine that wants the files.
    public bool KeepRecordingsAfterPublish { get; set; }

    /// Ceiling on what the recordings folder may hold in mp4s (GB). Above it
    /// the delivery pass evicts the oldest games - published ones first, then,
    /// loudly, unpublished ones - so a slow upload backlog can never fill a
    /// friend's disk. 0 = no ceiling.
    public double MaxRecordingsGb { get; set; } = 20;

    /// Free space to leave on the recordings drive (GB): the eviction above
    /// also runs while free space is under this, and a new game is not
    /// recorded at all when less than half of it is free.
    public double MinFreeGb { get; set; } = 10;

    /// Optional cap on the recorded picture height, aspect kept (3440x1440
    /// -> 2580x1080); the WGC engine scales on the GPU. 0 = native, and
    /// native is the default: people buy big screens to see them. A knob for
    /// one struggling machine (per-agent profile), not a policy.
    public int RecordMaxHeight { get; set; }

    /// Record the game's audio track (and ONLY the game's - captured from
    /// the game process via Windows process loopback, so Discord/music
    /// never enter the VOD). Needs Windows 10 2004+; falls back to
    /// video-only when unavailable.
    public bool RecordAudio { get; set; } = true;

    /// Capture engine for live-game recording. "ddagrab": ffmpeg's Desktop
    /// Duplication capture, zero-copy into NVENC - but the duplication
    /// session dies on exclusive-fullscreen display mode switches, which the
    /// segment supervisor absorbs as a seam. "wgc": Windows Graphics Capture
    /// (ScreenRecorderLib + Media Foundation hardware H264) - DWM-composited
    /// capture that mode switches and alt-tab cannot interrupt; falls back
    /// to ddagrab per segment if it won't start.
    public string CaptureBackend { get; set; } = "wgc";

    /// On an HDR desktop the WGC engine captures scRGB and tone-maps it to
    /// SDR on the GPU (deploy/screenrecorderlib), which is what makes Auto
    /// HDR / RTX HDR games record the way they look. Off = the stock 8-bit
    /// capture, which clips HDR to white; a support knob, not a preference.
    public bool HdrToneMap { get; set; } = true;

    /// Which queue kinds get recorded, comma-separated: ranked-solo,
    /// ranked-flex, normal (draft/blind/swiftplay/quickplay), aram, clash,
    /// coop-ai, urf, nexus-blitz, arena, brawl, doom-bots, custom (customs +
    /// Practice Tool), or all. Unknown/new queues only record under "all"
    /// (the skip log names their id so they can be added here).
    public string RecordQueues { get; set; } = "ranked-solo,normal";

    /// Publish each finished recording to the player's YouTube channel and
    /// register the link with the owning tracker - the storage-free review
    /// mode with the manual upload-and-paste step automated away. Needs a
    /// Google OAuth "Desktop app" client (id/secret below) plus a one-time
    /// "--youtube-auth" browser consent (refresh token lands next to the exe).
    /// Note: unaudited Google API projects get their uploads forced private
    /// regardless of the visibility asked for, until Google's audit clears
    /// the project.
    public bool YouTubeUpload { get; set; }
    public string YouTubeClientId { get; set; } = "";
    public string YouTubeClientSecret { get; set; } = "";

    /// Refresh token for the channel. Normally arrives from the tracker's
    /// agent profile (the channel owner authorized once, the token lives on
    /// the NAS); youtube-token.json next to the exe (--youtube-auth) is the
    /// fallback for the machine that did the authorizing.
    public string YouTubeRefreshToken { get; set; } = "";

    /// unlisted (default), private, or public.
    public string YouTubeVisibility { get; set; } = "unlisted";

    /// Open the finished game's review reel in the browser once it lands on
    /// its tracker - but only when the next game isn't already being queued
    /// for. On by default for every recording agent (the review is the point
    /// of the recording); queueing up straight away skips it, and it never
    /// runs on a renderer-only box.
    public bool PostGameReview { get; set; } = true;

    /// How long to let the end-of-game screens settle before deciding whether
    /// a review is wanted. Long enough that hitting "play again" immediately
    /// is read as "not now", short enough to still feel like part of the game.
    public int PostGameReviewDelaySec { get; set; } = 30;

    /// Roll straight into the next moment when a window ends (the default -
    /// the review plays itself and the hotkeys are the override). False parks
    /// the replay at every window's end and waits for a key instead.
    public bool PostGameReviewAutoAdvance { get; set; } = true;

    /// How long to keep waiting for the tracker to import the match (the
    /// poller lags the game by minutes). Queueing up at any point during the
    /// wait cancels the review.
    public int PostGameReviewWaitMin { get; set; } = 8;

    /// Open the League client automatically (through Riot's launcher) when
    /// render jobs are waiting, the client is closed, and nobody is at the
    /// keyboard - the agent-side half of wake-on-LAN: the NAS wakes the
    /// machine, this brings up the client the renders need. Requires "stay
    /// signed in" in the Riot client so the launch lands logged in.
    public bool AutoLaunchClient { get; set; } = true;

    /// The code the owner minted on their Data page: sent with the enrolment
    /// so this machine's key is theirs from the start. Single use on the
    /// server; harmless to keep sending afterwards.
    public string JoinCode { get; set; } = "";

    /// The keys appsettings.json set explicitly - the tracker's profile fills
    /// in around them, never over them.
    private readonly HashSet<string> _localKeys = new(StringComparer.OrdinalIgnoreCase);

    public static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";

    /// appsettings.json next to the exe, then LT_* environment variables on top.
    public static AgentConfig Load()
    {
        var config = new AgentConfig();
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var text = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<AgentConfig>(text, options) ?? config;
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var property in doc.RootElement.EnumerateObject()) config._localKeys.Add(property.Name);
        }

        if (Environment.GetEnvironmentVariable("LT_SERVER_URL") is { Length: > 0 } server) config.ServerUrl = server;
        if (Environment.GetEnvironmentVariable("LT_LEAGUE_PATH") is { Length: > 0 } league) config.LeaguePath = league;
        if (Environment.GetEnvironmentVariable("LT_FFMPEG_PATH") is { Length: > 0 } ffmpeg) config.FfmpegPath = ffmpeg;
        if (Environment.GetEnvironmentVariable("LT_MAX_WINDOWS") is { Length: > 0 } max && int.TryParse(max, out var m)) config.MaxWindowsPerJob = m;
        if (Environment.GetEnvironmentVariable("LT_RECORD") is { Length: > 0 } record) config.RecordGames = record is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_RENDER") is { Length: > 0 } render) config.RenderReplays = render is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_RECORDINGS_DIR") is { Length: > 0 } recDir) config.RecordingsDir = recDir;
        if (Environment.GetEnvironmentVariable("LT_RECORD_INPUTS") is { Length: > 0 } inputs) config.RecordInputs = inputs is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_RECORD_QUEUES") is { Length: > 0 } queues) config.RecordQueues = queues;
        if (Environment.GetEnvironmentVariable("LT_RECORD_AUDIO") is { Length: > 0 } audio) config.RecordAudio = audio is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_CAPTURE_BACKEND") is { Length: > 0 } backend) config.CaptureBackend = backend;
        if (Environment.GetEnvironmentVariable("LT_UPLOAD_INGAME_MBPS") is { Length: > 0 } mbps && double.TryParse(mbps, System.Globalization.CultureInfo.InvariantCulture, out var mbpsValue)) config.UploadInGameMbps = mbpsValue;
        if (Environment.GetEnvironmentVariable("LT_KEEP_RECORDINGS") is { Length: > 0 } keep) config.KeepRecordingsAfterPublish = keep is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_RECORD_MAX_HEIGHT") is { Length: > 0 } maxHeight && int.TryParse(maxHeight, out var maxHeightValue)) config.RecordMaxHeight = maxHeightValue;
        if (Environment.GetEnvironmentVariable("LT_HDR_TONEMAP") is { Length: > 0 } toneMap) config.HdrToneMap = toneMap is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_YOUTUBE_UPLOAD") is { Length: > 0 } yt) config.YouTubeUpload = yt is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_YOUTUBE_CLIENT_ID") is { Length: > 0 } ytId) config.YouTubeClientId = ytId;
        if (Environment.GetEnvironmentVariable("LT_YOUTUBE_CLIENT_SECRET") is { Length: > 0 } ytSecret) config.YouTubeClientSecret = ytSecret;
        if (Environment.GetEnvironmentVariable("LT_POSTGAME_REVIEW") is { Length: > 0 } review) config.PostGameReview = review is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_AUTO_LAUNCH_CLIENT") is { Length: > 0 } autoLaunch) config.AutoLaunchClient = autoLaunch is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_JOIN_CODE") is { Length: > 0 } joinCode) config.JoinCode = joinCode;

        if (config.AgentName is not { Length: > 0 }) config.AgentName = Environment.MachineName;
        return config;
    }

    /// The tracker's agent profile: string values keyed by property name.
    /// Local settings and environment overrides stay; only unset keys take the
    /// server's value. Returns the names that changed.
    public List<string> ApplyProfile(IReadOnlyDictionary<string, string> profile)
    {
        var applied = new List<string>();
        foreach (var (key, value) in profile)
        {
            if (_localKeys.Contains(key)) continue;
            if (typeof(AgentConfig).GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not { CanWrite: true } property) continue;
            object converted;
            try
            {
                converted = property.PropertyType == typeof(bool) ? value is "1" or "true" or "True"
                    : property.PropertyType == typeof(int) ? int.Parse(value)
                    : property.PropertyType == typeof(double) ? double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                    : value;
            }
            catch (FormatException)
            {
                Log.Warn($"Profile value for {key} is not a {property.PropertyType.Name}: \"{value}\"");
                continue;
            }
            if (Equals(property.GetValue(this), converted)) continue;
            property.SetValue(this, converted);
            applied.Add(property.Name);
        }
        return applied;
    }
}

/// What the agent is doing right now - one place the tray menu, the log and
/// the heartbeat all read from. Loops set it at their natural boundaries.
public static class AgentStatus
{
    private static readonly object Gate = new();
    private static string _state = "starting";
    private static string? _detail;

    public static DateTime? LastRecordingUtc { get; set; }
    public static string? LastError { get; set; }
    public static bool YouTubeReady { get; set; } = true;
    public static event Action? Changed;

    public static (string State, string? Detail) Current
    {
        get { lock (Gate) return (_state, _detail); }
    }

    public static void Set(string state, string? detail = null)
    {
        lock (Gate)
        {
            if (_state == state && _detail == detail) return;
            _state = state;
            _detail = detail;
        }
        Changed?.Invoke();
    }
}

/// Console when one is attached (dev runs), and always agent.log next to the
/// exe - the published agent is a WinExe with no console at all.
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "agent.log");

    static Log()
    {
        try { if (new FileInfo(LogPath) is { Exists: true, Length: > 5_000_000 }) File.Delete(LogPath); } catch { /* keep logging best-effort */ }
    }

    /// The newest maxBytes of the log, for shipping to the tracker on
    /// request - the owner's way to see a customer's machine without asking
    /// them to find and send a file.
    public static string Tail(int maxBytes)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(LogPath)) return "";
                using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var start = Math.Max(0, stream.Length - maxBytes);
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                // Cutting into a line is worse than losing it: start at the next full line.
                return start == 0 ? text : text[(text.IndexOf((char)10) + 1)..];
            }
            catch (Exception ex)
            {
                return $"(could not read {LogPath}: {ex.Message})";
            }
        }
    }

    public static void Info(string message) => Write($"[{DateTime.Now:HH:mm:ss}] {message}");
    public static void Warn(string message) => Write($"[{DateTime.Now:HH:mm:ss}] WARN {message}");
    public static void Error(string message) => Write($"[{DateTime.Now:HH:mm:ss}] ERROR {message}");

    private static void Write(string line)
    {
        Console.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { /* disk hiccup - console still got it */ }
        }
    }
}
