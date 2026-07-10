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
