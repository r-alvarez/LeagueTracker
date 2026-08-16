using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeagueTracker.Api.Accounts;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

public enum AgentKeyStatus { Pending, Approved, Revoked }

/// One enrolled agent: what it called itself, where from, and whether a
/// human said yes. Only the SHA-256 of the key is kept - the key itself
/// exists on the agent's disk and nowhere else.
public sealed class AgentKeyRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Machine { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public AgentKeyStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? DecidedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public string? LastIp { get; set; }
    public string? Note { get; set; }
}

/// Agents enrol themselves with a key they generated; the owner approves
/// them on the Data page. Cloudflare Access is bypassed for /api/agent/*
/// so a fresh machine can knock with nothing but the URL - which is why
/// everything under that prefix except enrol/ping/release demands an
/// approved key. Stored as <DataRoot>/agents.json.
public sealed class AgentKeyStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private const int MaxPending = 20;
    private const int MaxPendingPerIp = 3;

    private readonly object _gate = new();
    private readonly List<AgentKeyRecord> _records;
    private readonly string _path;
    private readonly ILogger<AgentKeyStore> _log;

    public AgentKeyStore(IOptions<AccountsOptions> accounts, AccountRegistry registry, IWebHostEnvironment env, ILogger<AgentKeyStore> log)
    {
        _log = log;
        var root = accounts.Value.DataRoot is { Length: > 0 } r
            ? (Path.IsPathRooted(r) ? r : Path.Combine(env.ContentRootPath, r))
            : registry.Default.DataDir;
        _path = Path.Combine(root, "agents.json");
        _records = Load();
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public IReadOnlyList<AgentKeyRecord> All { get { lock (_gate) return [.. _records.OrderBy(r => r.Status).ThenByDescending(r => r.CreatedUtc)]; } }

    /// The record for a presented key, whatever its status; null = unknown.
    public AgentKeyRecord? Find(string key)
    {
        var hash = Hash(key);
        lock (_gate) return _records.FirstOrDefault(r => r.KeyHash == hash);
    }

    /// Enrol or re-announce. The same key always maps to the same record,
    /// so an agent that restarts before approval just asks again.
    public (AgentKeyRecord Record, bool Created, string? Refusal) Enroll(string key, string name, string machine, string? ip)
    {
        var hash = Hash(key);
        lock (_gate)
        {
            if (_records.FirstOrDefault(r => r.KeyHash == hash) is { } existing)
            {
                existing.LastSeenUtc = DateTime.UtcNow;
                existing.LastIp = ip;
                Save();
                return (existing, false, null);
            }
            var pending = _records.Where(r => r.Status is AgentKeyStatus.Pending).ToList();
            if (pending.Count >= MaxPending) return (null!, false, "too many agents are waiting for approval - ask the owner to clear the list");
            if (ip is not null && pending.Count(r => r.LastIp == ip) >= MaxPendingPerIp) return (null!, false, "too many pending enrolments from this address");
            var record = new AgentKeyRecord
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = name is { Length: > 0 } ? name[..Math.Min(name.Length, 64)] : machine,
                Machine = machine[..Math.Min(machine.Length, 64)],
                KeyHash = hash,
                Status = AgentKeyStatus.Pending,
                CreatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                LastIp = ip,
            };
            _records.Add(record);
            Save();
            _log.LogInformation("Agent enrolment pending: {Name} ({Machine}) from {Ip}", record.Name, record.Machine, ip);
            return (record, true, null);
        }
    }

    public bool Decide(string id, AgentKeyStatus status, string? note = null)
    {
        lock (_gate)
        {
            if (_records.FirstOrDefault(r => r.Id == id) is not { } record) return false;
            record.Status = status;
            record.DecidedUtc = DateTime.UtcNow;
            if (note is not null) record.Note = note;
            Save();
            _log.LogInformation("Agent {Name}: {Status}", record.Name, status);
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var removed = _records.RemoveAll(r => r.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public void Touch(AgentKeyRecord record, string? ip)
    {
        lock (_gate)
        {
            // Cheap: only persist when the minute changes, not on every poll.
            var stamp = DateTime.UtcNow;
            var wasMinute = record.LastSeenUtc?.ToString("yyyyMMddHHmm");
            record.LastSeenUtc = stamp;
            record.LastIp = ip;
            if (wasMinute != stamp.ToString("yyyyMMddHHmm")) Save();
        }
    }

    private List<AgentKeyRecord> Load()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<AgentKeyRecord>>(File.ReadAllText(_path), Json) ?? []; }
        catch (Exception ex) { _log.LogError("agents.json is unreadable ({Message}) - starting with no enrolled agents", ex.Message); return []; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_records, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// Guards /api/agent/*: the slice Cloudflare Access lets through unauthenticated
/// so agents can enrol with nothing but the URL. Anonymous: enrol, its status
/// poll, ping, and the release feed (public builds anyway). Everything else
/// needs an approved key in X-Agent-Key.
public sealed class AgentAuthMiddleware(RequestDelegate next, AgentKeyStore keys)
{
    public const string HeaderName = "X-Agent-Key";
    public const string ItemKey = "agent-key-record";

    private static readonly string[] Anonymous = ["/api/agent/enroll", "/api/agent/ping", "/api/agent/release"];

    public async Task InvokeAsync(HttpContext http)
    {
        var path = http.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/agent/", StringComparison.OrdinalIgnoreCase)) { await next(http); return; }

        var key = http.Request.Headers[HeaderName].FirstOrDefault();
        var record = key is { Length: >= 16 } ? keys.Find(key) : null;
        if (record is not null && record.Status is AgentKeyStatus.Approved)
        {
            keys.Touch(record, http.Connection.RemoteIpAddress?.ToString());
            http.Items[ItemKey] = record;
            await next(http);
            return;
        }
        if (Anonymous.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
        {
            await next(http);
            return;
        }
        http.Response.StatusCode = record is null ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(record is null
            ? "{\"error\":\"agent key missing or unknown - enrol first\"}"
            : $"{{\"error\":\"agent is {record.Status.ToString().ToLowerInvariant()}\"}}");
    }
}
