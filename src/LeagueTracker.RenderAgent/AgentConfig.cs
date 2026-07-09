using System.Text.Json;

namespace LeagueTracker.RenderAgent;

public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "http://localhost:5170";
    public string LeaguePath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string AgentName { get; set; } = "";
    public int PollSeconds { get; set; } = 60;
    public int CaptureFramerate { get; set; } = 30;
    public int MaxWindowsPerJob { get; set; }

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

        config.ServerUrl = config.ServerUrl.TrimEnd('/');
        if (config.AgentName is not { Length: > 0 }) config.AgentName = Environment.MachineName;
        return config;
    }
}

public static class Log
{
    public static void Info(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    public static void Warn(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN {message}");
    public static void Error(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR {message}");
}
