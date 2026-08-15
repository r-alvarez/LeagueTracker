using System.Security.Cryptography;
using LeagueTracker.Api.Accounts;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

/// What the tracker hands its agents at startup and how it publishes agent
/// builds. Profile keys are AgentConfig property names (case-insensitive,
/// string values - the agent converts); the friend's local appsettings.json
/// still wins for anything it sets, so this is the central "defaults +
/// secrets" store: YouTube client + refresh token, recording prefix, queues.
/// Set via env: Agent__Profile__YouTubeClientId=... Agent__ReleaseDir=/agent-releases
public sealed class AgentOptions
{
    public Dictionary<string, string> Profile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// Folder holding LeagueTracker.RenderAgent-<version>.zip builds. Blank =
    /// <Accounts:DataRoot or the default account's DataDir>/agent-releases -
    /// process-wide, not per account.
    public string ReleaseDir { get; set; } = "";

    /// Mirror the newest agent build from GitHub Releases into ReleaseDir.
    /// One tracker per shared folder - the others just read.
    public bool SyncReleases { get; set; }
    public string GitHubRepo { get; set; } = "r-alvarez/LeagueTracker";
}

public sealed record AgentHeartbeat(
    string Agent, string Version, string Role, bool Paused, string State, string? Detail,
    DateTime? LastRecordingUtc, bool YouTubeReady, string? LastError, string? Machine, string? User);

public sealed record AgentRelease(string Version, string File, long SizeBytes, string Sha256, DateTime ModifiedUtc);

/// In-memory: agents re-announce every poll, so a restart just waits a
/// minute for the picture to refill. Nothing here is worth a table.
public sealed class AgentRegistry(IOptions<AgentOptions> options, IOptions<AccountsOptions> accounts, AccountRegistry registry, IWebHostEnvironment env)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (AgentHeartbeat Beat, DateTime SeenUtc)> _agents = new(StringComparer.OrdinalIgnoreCase);
    private (string Path, DateTime ModifiedUtc, string Sha)? _shaCache;

    public IReadOnlyDictionary<string, string> Profile => options.Value.Profile;

    public string ReleaseDir => options.Value.ReleaseDir is { Length: > 0 } dir ? dir
        : accounts.Value.DataRoot is { Length: > 0 } root ? Path.Combine(Path.IsPathRooted(root) ? root : Path.Combine(env.ContentRootPath, root), "agent-releases")
        : Path.Combine(registry.Default.DataDir, "agent-releases");

    public void Record(AgentHeartbeat beat)
    {
        lock (_gate) _agents[beat.Agent] = (beat, DateTime.UtcNow);
    }

    public List<object> Snapshot()
    {
        lock (_gate)
        {
            return [.. _agents.Values.OrderBy(a => a.Beat.Agent).Select(a => (object)new
            {
                a.Beat.Agent, a.Beat.Version, a.Beat.Role, a.Beat.Paused, a.Beat.State, a.Beat.Detail,
                a.Beat.LastRecordingUtc, a.Beat.YouTubeReady, a.Beat.LastError, a.Beat.Machine, a.Beat.User,
                SeenUtc = a.SeenUtc,
                // Two missed polls = gone. The agent polls every 60s.
                Online = DateTime.UtcNow - a.SeenUtc < TimeSpan.FromMinutes(3),
            })];
        }
    }

    /// The newest build in ReleaseDir by version, or null when nothing is
    /// published. Files are named LeagueTracker.RenderAgent-<version>.zip
    /// (deploy/publish-agent.ps1 writes them).
    public AgentRelease? Latest()
    {
        if (!Directory.Exists(ReleaseDir)) return null;
        var best = Directory.EnumerateFiles(ReleaseDir, "LeagueTracker.RenderAgent-*.zip")
            .Select(f => (Path: f, Version: ParseVersion(f)))
            .Where(f => f.Version is not null)
            .OrderByDescending(f => f.Version)
            .FirstOrDefault();
        if (best.Path is null) return null;

        var info = new FileInfo(best.Path);
        return new AgentRelease(best.Version!.ToString(), info.Name, info.Length, Sha256Of(info), info.LastWriteTimeUtc);
    }

    public string? ReleasePath(string file)
    {
        // The name comes from the client - keep it inside ReleaseDir.
        if (Path.GetFileName(file) != file || !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        var full = Path.Combine(ReleaseDir, file);
        return File.Exists(full) ? full : null;
    }

    private static Version? ParseVersion(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        return dash > 0 && Version.TryParse(name[(dash + 1)..], out var v) ? v : null;
    }

    private string Sha256Of(FileInfo info)
    {
        lock (_gate)
        {
            if (_shaCache is { } c && c.Path == info.FullName && c.ModifiedUtc == info.LastWriteTimeUtc) return c.Sha;
        }
        using var stream = info.OpenRead();
        var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        lock (_gate) _shaCache = (info.FullName, info.LastWriteTimeUtc, sha);
        return sha;
    }
}
