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

    /// Where finished recordings (mp4 + sidecar json + thumbnail) land.
    /// Blank = <My Videos>\LeagueTracker.
    public string RecordingsDir { get; set; } = "";

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

    /// Record the game's audio track (and ONLY the game's - captured from
    /// the game process via Windows process loopback, so Discord/music
    /// never enter the VOD). Needs Windows 10 2004+; falls back to
    /// video-only when unavailable.
    public bool RecordAudio { get; set; } = true;

    /// Which queue kinds get recorded, comma-separated: ranked-solo,
    /// ranked-flex, normal (draft/blind/swiftplay/quickplay), aram, clash,
    /// coop-ai, urf, nexus-blitz, arena, brawl, doom-bots, custom (customs +
    /// Practice Tool), or all. Unknown/new queues only record under "all"
    /// (the skip log names their id so they can be added here).
    public string RecordQueues { get; set; } = "ranked-solo,normal";

    /// Cloudflare Access service token (Zero Trust > Access > Service Auth) -
    /// lets the agent through the Access wall the trackers sit behind. Blank =
    /// no Access in front (dev against localhost).
    public string CfAccessClientId { get; set; } = "";
    public string CfAccessClientSecret { get; set; } = "";

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
            config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), options) ?? config;
        }

        if (Environment.GetEnvironmentVariable("LT_SERVER_URL") is { Length: > 0 } server) config.ServerUrl = server;
        if (Environment.GetEnvironmentVariable("LT_LEAGUE_PATH") is { Length: > 0 } league) config.LeaguePath = league;
        if (Environment.GetEnvironmentVariable("LT_FFMPEG_PATH") is { Length: > 0 } ffmpeg) config.FfmpegPath = ffmpeg;
        if (Environment.GetEnvironmentVariable("LT_MAX_WINDOWS") is { Length: > 0 } max && int.TryParse(max, out var m)) config.MaxWindowsPerJob = m;
        if (Environment.GetEnvironmentVariable("LT_RECORD") is { Length: > 0 } record) config.RecordGames = record is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_RECORDINGS_DIR") is { Length: > 0 } recDir) config.RecordingsDir = recDir;
        if (Environment.GetEnvironmentVariable("LT_RECORD_INPUTS") is { Length: > 0 } inputs) config.RecordInputs = inputs is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_RECORD_QUEUES") is { Length: > 0 } queues) config.RecordQueues = queues;
        if (Environment.GetEnvironmentVariable("LT_RECORD_AUDIO") is { Length: > 0 } audio) config.RecordAudio = audio is not ("0" or "false");
        if (Environment.GetEnvironmentVariable("LT_CF_ACCESS_CLIENT_ID") is { Length: > 0 } cfId) config.CfAccessClientId = cfId;
        if (Environment.GetEnvironmentVariable("LT_CF_ACCESS_CLIENT_SECRET") is { Length: > 0 } cfSecret) config.CfAccessClientSecret = cfSecret;

        if (config.AgentName is not { Length: > 0 }) config.AgentName = Environment.MachineName;
        return config;
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
