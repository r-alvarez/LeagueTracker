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

    /// Per-agent overrides layered on Profile, keyed by the agent's key id
    /// (shown on the Machines page): Agent__Profiles__<id>__YouTubeClientId=...
    /// gives one machine its own YouTube OAuth client (own Google project =
    /// own daily quota) while everything else stays shared. Blank values do
    /// not override. The id and not the enrolled name, because the name is
    /// whatever the machine typed - any user could enrol a key under another
    /// machine's name and be handed its credentials (audit T-N1).
    public Dictionary<string, Dictionary<string, string>> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// The shared profile with the keyed agent's overrides on top (blank
    /// overrides ignored: an unset stack env var must not blank a value).
    public IReadOnlyDictionary<string, string> ProfileFor(string? agentId)
    {
        if (agentId is not { Length: > 0 } || !Profiles.TryGetValue(agentId, out var overrides)) return Profile;
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

public sealed record AgentLogInfo(string File, DateTime WhenUtc, long SizeBytes);

/// One agent as the Data page sees it: its last heartbeat plus whether it
/// is still reporting. Id is the key record's - the identity the server
/// vouches for; Agent is the name from the record, not the heartbeat.
public sealed record AgentLive(
    string Id, string Agent, string Version, string Role, bool Paused, string State, string? Detail,
    DateTime? LastRecordingUtc, bool YouTubeReady, string? LastError, string? Machine, string? User,
    string? Owner, bool Mine, DateTime SeenUtc, bool Online);

/// Installer is the Setup.exe published beside the zip (null when a
/// release predates it or it was not mirrored) - for new machines; the zip
/// is what installed agents update from.
public sealed record AgentRelease(string Version, string File, long SizeBytes, string Sha256, DateTime ModifiedUtc, string? Installer = null, long InstallerSizeBytes = 0);

/// In-memory: agents re-announce every poll, so a restart just waits a
/// minute for the picture to refill. Nothing here is worth a table.
public sealed class AgentRegistry(IOptions<AgentOptions> options, IOptions<AccountsOptions> accounts, AccountRegistry registry, IWebHostEnvironment env)
{
    private readonly object _gate = new();
    // Keyed by the agent's key record id - the identity the server vouches
    // for - never by the name the heartbeat carries.
    private readonly Dictionary<string, (AgentKeyRecord Key, AgentHeartbeat Beat, DateTime SeenUtc)> _agents = new(StringComparer.OrdinalIgnoreCase);
    // The last error the owner has acknowledged, per key - hidden until a
    // different one arrives (in memory: a dismissed transient never matters
    // after a restart).
    private readonly Dictionary<string, string> _dismissedError = new(StringComparer.OrdinalIgnoreCase);
    // A one-shot command queued for a key (id -> (token, command)), delivered
    // on the heartbeat. The token lets the agent run it once.
    private readonly Dictionary<string, (string Token, string Command)> _command = new(StringComparer.OrdinalIgnoreCase);
    private (string Path, DateTime ModifiedUtc, string Sha)? _shaCache;

    public IReadOnlyDictionary<string, string> Profile => options.Value.Profile;

    public IReadOnlyDictionary<string, string> ProfileFor(string? agentId) => options.Value.ProfileFor(agentId);

    // Override blocks that name no key: a machine name left over from when
    // overrides were keyed by name, or a typo - either way the operator
    // wants to hear about it at boot, not when the quota runs out.
    public IEnumerable<string> OverridesForUnknownKeys(AgentKeyStore keys) => options.Value.Profiles.Keys.Where(id => keys.ById(id) is null);

    public string ReleaseDir => options.Value.ReleaseDir is { Length: > 0 } dir ? dir : Path.Combine(DataRootDir, "agent-releases");

    /// Where process-wide agent things live: the accounts' data root, or the
    /// default account's folder on a single-account tracker.
    private string DataRootDir => accounts.Value.DataRoot is { Length: > 0 } root
        ? (Path.IsPathRooted(root) ? root : Path.Combine(env.ContentRootPath, root))
        : registry.Default.DataDir;

    public void Record(AgentKeyRecord key, AgentHeartbeat beat)
    {
        lock (_gate) _agents[key.Id] = (key, beat, DateTime.UtcNow);
    }

    /// Where agents' shipped logs live: <data root>/agent-logs/<key id>/
    /// <utc stamp>.log, newest few kept. The key id, not the name: two
    /// owners' machines may share a name.
    public string LogDir => Path.Combine(DataRootDir, "agent-logs");

    private const int LogsKeptPerAgent = 5;

    /// Stores a log the agent shipped on the owner's "sendlog" command.
    /// Returns the stored file name.
    public string StoreLog(string agent, string text)
    {
        var dir = Path.Combine(LogDir, SafeName(agent));
        Directory.CreateDirectory(dir);
        var file = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}.log";
        File.WriteAllText(Path.Combine(dir, file), text);
        foreach (var old in Directory.EnumerateFiles(dir, "*.log").OrderByDescending(f => f).Skip(LogsKeptPerAgent))
        {
            try { File.Delete(old); } catch { /* next store retries */ }
        }
        return file;
    }

    public List<AgentLogInfo> Logs(string agent)
    {
        var dir = Path.Combine(LogDir, SafeName(agent));
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "*.log")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name)
            .Select(f => new AgentLogInfo(f.Name, f.LastWriteTimeUtc, f.Length))];
    }

    public string? LogPath(string agent, string file)
    {
        // The name is ours (a stamp + .log); anything else is not a log we stored.
        if (!System.Text.RegularExpressions.Regex.IsMatch(file, @"^\d{8}-\d{6}\.log$")) return null;
        var path = Path.Combine(LogDir, SafeName(agent), file);
        return File.Exists(path) ? path : null;
    }

    /// Agent names are machine names by default and can hold anything a
    /// user typed; the folder name keeps only what every file system takes.
    private static string SafeName(string agent)
    {
        var chars = agent.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return chars.Length is 0 ? "_" : new string(chars);
    }

    /// Hide the agent's current last error until a different one comes in.
    /// Queue a command for an agent's next heartbeat (only "restart" today).
    /// A fresh token each time so a re-press fires again; the agent keeps the
    /// last token it ran so a relaunch does not loop.
    public bool Queue(string keyId, string command)
    {
        lock (_gate)
        {
            if (!_agents.ContainsKey(keyId)) return false;
            _command[keyId] = (Guid.NewGuid().ToString("N")[..12], command);
            return true;
        }
    }

    /// The command pending for an agent (if any) - read by the heartbeat.
    public (string Token, string Command)? PendingCommand(string keyId)
    {
        lock (_gate) return _command.TryGetValue(keyId, out var c) ? c : null;
    }

    public bool DismissError(string keyId)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(keyId, out var a) || a.Beat.LastError is not { Length: > 0 } error) return false;
            _dismissedError[keyId] = error;
            return true;
        }
    }

    // Everything (admin's view, and the agent-to-agent listing).
    public List<AgentLive> Snapshot() => SnapshotFor(null, admin: true);

    // The machines an owner may see: theirs, plus every renderer (a renderer
    // serves everyone, so everyone gets to know it exists); an admin sees
    // all. Windows user names never leave the owner's own view.
    public List<AgentLive> SnapshotFor(string? ownerUserId, bool admin)
    {
        lock (_gate)
        {
            return [.. _agents.Values
                .Where(a => admin || a.Key.Role is Registry.AgentRole.Renderer || (ownerUserId is not null && a.Key.OwnerUserId == ownerUserId))
                .OrderBy(a => a.Key.Name)
                .Select(a => Live(a, ownerUserId, admin))];
        }
    }

    /// The heartbeat picture for one enrolled key, as its owner (or an admin) sees it.
    public AgentLive? Find(string keyId, string? viewerUserId, bool admin)
    {
        lock (_gate) return _agents.TryGetValue(keyId, out var a) ? Live(a, viewerUserId, admin) : null;
    }

    private AgentLive Live((AgentKeyRecord Key, AgentHeartbeat Beat, DateTime SeenUtc) a, string? viewerUserId, bool admin) => new(
        a.Key.Id, a.Key.Name, a.Beat.Version, a.Beat.Role, a.Beat.Paused, a.Beat.State, a.Beat.Detail,
        a.Beat.LastRecordingUtc, a.Beat.YouTubeReady,
        _dismissedError.TryGetValue(a.Key.Id, out var d) && d == a.Beat.LastError ? null : a.Beat.LastError,
        a.Beat.Machine,
        admin || (viewerUserId is not null && a.Key.OwnerUserId == viewerUserId) ? a.Beat.User : null,
        a.Key.OwnerUserId,
        viewerUserId is not null && a.Key.OwnerUserId == viewerUserId,
        a.SeenUtc,
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
