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

    /// Per-agent overrides layered on Profile, keyed by the agent's enrolled
    /// name: Agent__Profiles__<name>__YouTubeClientId=... gives one machine
    /// its own YouTube OAuth client (own Google project = own daily quota)
    /// while everything else stays shared. Blank values do not override.
    public Dictionary<string, Dictionary<string, string>> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// The shared profile with the named agent's overrides on top (blank
    /// overrides ignored: an unset stack env var must not blank a value).
    public IReadOnlyDictionary<string, string> ProfileFor(string? agentName)
    {
        if (agentName is not { Length: > 0 } || !Profiles.TryGetValue(agentName, out var overrides)) return Profile;
        var merged = new Dictionary<string, string>(Profile, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overrides)
        {
            if (value is { Length: > 0 }) merged[key] = value;
        }
        return merged;
    }

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

/// One agent as the Data page sees it: its last heartbeat plus whether it
/// is still reporting.
public sealed record AgentLive(
    string Agent, string Version, string Role, bool Paused, string State, string? Detail,
    DateTime? LastRecordingUtc, bool YouTubeReady, string? LastError, string? Machine, string? User,
    DateTime SeenUtc, bool Online);

/// Installer is the Setup.exe published beside the zip (null when a
/// release predates it or it was not mirrored) - for new machines; the zip
/// is what installed agents update from.
public sealed record AgentRelease(string Version, string File, long SizeBytes, string Sha256, DateTime ModifiedUtc, string? Installer = null, long InstallerSizeBytes = 0);

/// In-memory: agents re-announce every poll, so a restart just waits a
/// minute for the picture to refill. Nothing here is worth a table.
public sealed class AgentRegistry(IOptions<AgentOptions> options, IOptions<AccountsOptions> accounts, AccountRegistry registry, IWebHostEnvironment env)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (AgentHeartbeat Beat, DateTime SeenUtc)> _agents = new(StringComparer.OrdinalIgnoreCase);
    // The last error the owner has acknowledged, per agent - hidden until a
    // different one arrives (in memory: a dismissed transient never matters
    // after a restart).
    private readonly Dictionary<string, string> _dismissedError = new(StringComparer.OrdinalIgnoreCase);
    // A one-shot command queued for an agent (name -> (token, command)),
    // delivered on the heartbeat. The token lets the agent run it once.
    private readonly Dictionary<string, (string Token, string Command)> _command = new(StringComparer.OrdinalIgnoreCase);
    private (string Path, DateTime ModifiedUtc, string Sha)? _shaCache;

    public IReadOnlyDictionary<string, string> Profile => options.Value.Profile;

    public IReadOnlyDictionary<string, string> ProfileFor(string? agentName) => options.Value.ProfileFor(agentName);

    public string ReleaseDir => options.Value.ReleaseDir is { Length: > 0 } dir ? dir
        : accounts.Value.DataRoot is { Length: > 0 } root ? Path.Combine(Path.IsPathRooted(root) ? root : Path.Combine(env.ContentRootPath, root), "agent-releases")
        : Path.Combine(registry.Default.DataDir, "agent-releases");

    public void Record(AgentHeartbeat beat)
    {
        lock (_gate) _agents[beat.Agent] = (beat, DateTime.UtcNow);
    }

    /// Hide the agent's current last error until a different one comes in.
    /// Queue a command for an agent's next heartbeat (only "restart" today).
    /// A fresh token each time so a re-press fires again; the agent keeps the
    /// last token it ran so a relaunch does not loop.
    public bool Queue(string agent, string command)
    {
        lock (_gate)
        {
            if (!_agents.ContainsKey(agent)) return false;
            _command[agent] = (Guid.NewGuid().ToString("N")[..12], command);
            return true;
        }
    }

    /// The command pending for an agent (if any) - read by the heartbeat.
    public (string Token, string Command)? PendingCommand(string agent)
    {
        lock (_gate) return _command.TryGetValue(agent, out var c) ? c : null;
    }

    public bool DismissError(string agent)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(agent, out var a) || a.Beat.LastError is not { Length: > 0 } error) return false;
            _dismissedError[agent] = error;
            return true;
        }
    }

    public List<AgentLive> Snapshot()
    {
        lock (_gate) return [.. _agents.Values.OrderBy(a => a.Beat.Agent).Select(Live)];
    }

    /// The heartbeat picture for an enrolled key: the agent that reports
    /// under the key's name, or failing that from the key's machine (an
    /// agent renamed in its settings still runs on the same PC).
    public AgentLive? Find(string name, string? machine)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(name, out var byName)) return Live(byName);
            var byMachine = _agents.Values.FirstOrDefault(a => machine is { Length: > 0 } && string.Equals(a.Beat.Machine, machine, StringComparison.OrdinalIgnoreCase));
            return byMachine.Beat is null ? null : Live(byMachine);
        }
    }

    private AgentLive Live((AgentHeartbeat Beat, DateTime SeenUtc) a) => new(
        a.Beat.Agent, a.Beat.Version, a.Beat.Role, a.Beat.Paused, a.Beat.State, a.Beat.Detail,
        a.Beat.LastRecordingUtc, a.Beat.YouTubeReady,
        _dismissedError.TryGetValue(a.Beat.Agent, out var d) && d == a.Beat.LastError ? null : a.Beat.LastError,
        a.Beat.Machine, a.Beat.User, a.SeenUtc,
        // Two missed polls = gone. The agent polls every 60s.
        Online: DateTime.UtcNow - a.SeenUtc < TimeSpan.FromMinutes(3));

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
        var installer = new FileInfo(Path.Combine(ReleaseDir, $"LeagueTracker.Agent-Setup-{best.Version}.exe"));
        return new AgentRelease(best.Version!.ToString(), info.Name, info.Length, Sha256Of(info), info.LastWriteTimeUtc,
            installer.Exists ? installer.Name : null, installer.Exists ? installer.Length : 0);
    }

    public string? ReleasePath(string file)
    {
        // The name comes from the client - keep it inside ReleaseDir, and only
        // the two shapes we publish.
        if (Path.GetFileName(file) != file) return null;
        var isZip = file.StartsWith("LeagueTracker.RenderAgent-") && file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var isSetup = file.StartsWith("LeagueTracker.Agent-Setup-") && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (!isZip && !isSetup) return null;
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
