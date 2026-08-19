using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeagueTracker.Api.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

public enum AgentKeyStatus { Pending, Approved, Revoked }

// One enrolled agent: what it called itself, where from, whose it is, and
// whether a human said yes. Only the SHA-256 of the key is kept - the key
// itself exists on the agent's disk and nowhere else.
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
    // The User this machine belongs to. Null only for keys approved before
    // ownership existed - tolerated while Agents:AllowUnbound is on.
    public string? OwnerUserId { get; set; }
    // A recorder acts on its owner's accounts; a renderer reaches every
    // account's render endpoints and nothing else.
    public AgentRole Role { get; set; }
    public bool IsBound => OwnerUserId is { Length: > 0 };
}

public sealed class AgentsOptions
{
    // Rollout switch, off since the 2026-08 cutover: an unbound key is refused
    // everywhere account-scoped. On only lets keys approved before ownership
    // existed keep working on every account until the owner assigns them.
    public bool AllowUnbound { get; set; }
}

// Agents enrol themselves with a key they generated; a join code minted by a
// signed-in owner makes the record theirs at birth, and the owner approves it
// on their Data page. Held in memory (every agent request looks a key up),
// written through to registry.db; a first boot imports the old agents.json.
public sealed class AgentKeyStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private const int MaxPending = 20;
    private const int MaxPendingPerIp = 3;
    private static readonly TimeSpan JoinCodeLife = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly List<AgentKeyRecord> _records;
    private readonly RegistryDatabase _registry;
    private readonly IOptions<AgentsOptions> _options;
    private readonly ILogger<AgentKeyStore> _log;

    public AgentKeyStore(RegistryDatabase registry, IOptions<AgentsOptions> options, ILogger<AgentKeyStore> log)
    {
        _registry = registry;
        _options = options;
        _log = log;
        using var db = registry.Open();
        _records = db.AgentKeys.AsNoTracking().ToList();
        if (_records is []) _records = ImportLegacyJson(db);
    }

    public bool AllowUnbound => _options.Value.AllowUnbound;

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public IReadOnlyList<AgentKeyRecord> All { get { lock (_gate) return [.. _records.OrderBy(r => r.Status).ThenByDescending(r => r.CreatedUtc)]; } }

    public IReadOnlyList<AgentKeyRecord> OwnedBy(string userId) { lock (_gate) return [.. _records.Where(r => r.OwnerUserId == userId).OrderBy(r => r.Status).ThenByDescending(r => r.CreatedUtc)]; }

    // The record for a presented key, whatever its status; null = unknown.
    public AgentKeyRecord? Find(string key)
    {
        var hash = Hash(key);
        lock (_gate) return _records.FirstOrDefault(r => r.KeyHash == hash);
    }

    public AgentKeyRecord? ById(string id) { lock (_gate) return _records.FirstOrDefault(r => r.Id == id); }

    // Enrol or re-announce. The same key always maps to the same record, so
    // an agent that restarts before approval just asks again. A valid join
    // code binds a new record (or an unbound old one) to the code's owner.
    public (AgentKeyRecord Record, bool Created, string? Refusal) Enroll(string key, string name, string machine, string? ip, string? joinCode)
    {
        var hash = Hash(key);
        lock (_gate)
        {
            var code = joinCode is { Length: > 0 } ? TakeJoinCode(joinCode) : null;
            if (_records.FirstOrDefault(r => r.KeyHash == hash) is { } existing)
            {
                existing.LastSeenUtc = DateTime.UtcNow;
                existing.LastIp = ip;
                if (code is not null && !existing.IsBound)
                {
                    existing.OwnerUserId = code.OwnerUserId;
                    existing.Role = code.Role;
                    MarkUsed(code, existing.Id);
                }
                Persist(existing);
                return (existing, false, null);
            }
            var pending = _records.Where(r => r.Status is AgentKeyStatus.Pending).ToList();
            if (pending.Count >= MaxPending) return (null!, false, "too many agents are waiting for approval - ask the owner to clear the list");
            if (ip is not null && pending.Count(r => r.LastIp == ip) >= MaxPendingPerIp) return (null!, false, "too many pending enrolments from this address");
            var record = new AgentKeyRecord
            {
                Id = Ids.New(),
                Name = name is { Length: > 0 } ? name[..Math.Min(name.Length, 64)] : machine,
                Machine = machine[..Math.Min(machine.Length, 64)],
                KeyHash = hash,
                Status = AgentKeyStatus.Pending,
                CreatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                LastIp = ip,
                OwnerUserId = code?.OwnerUserId,
                Role = code?.Role ?? AgentRole.Recorder,
            };
            _records.Add(record);
            Persist(record);
            if (code is not null) MarkUsed(code, record.Id);
            _log.LogInformation("Agent enrolment pending: {Name} ({Machine}) from {Ip}{Owner}", record.Name, record.Machine, ip, record.IsBound ? $" for user {record.OwnerUserId}" : " (unbound)");
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
            Persist(record);
            _log.LogInformation("Agent {Name}: {Status}", record.Name, status);
            return true;
        }
    }

    // Admin's hand: the owner (and role) of a key, for the machines enrolled
    // before ownership existed or when a friend enrolled without a code.
    public bool Assign(string id, string? ownerUserId, AgentRole? role)
    {
        lock (_gate)
        {
            if (_records.FirstOrDefault(r => r.Id == id) is not { } record) return false;
            record.OwnerUserId = ownerUserId is { Length: > 0 } ? ownerUserId : null;
            if (role is { } r) record.Role = r;
            Persist(record);
            _log.LogInformation("Agent {Name}: owner {Owner}, role {Role}", record.Name, record.OwnerUserId ?? "(none)", record.Role);
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var removed = _records.RemoveAll(r => r.Id == id) > 0;
            if (removed)
            {
                using var db = _registry.Open();
                db.AgentKeys.Where(k => k.Id == id).ExecuteDelete();
            }
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
            if (wasMinute != stamp.ToString("yyyyMMddHHmm")) Persist(record);
        }
    }

    // --- Join codes -------------------------------------------------------------

    public JoinCode MintJoinCode(string ownerUserId, AgentRole role)
    {
        var code = new JoinCode
        {
            Code = NewCode(),
            OwnerUserId = ownerUserId,
            Role = role,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow + JoinCodeLife,
        };
        using var db = _registry.Open();
        db.JoinCodes.Add(code);
        // Anything expired or used a day ago is noise.
        var cutoff = DateTime.UtcNow.AddDays(-1);
        db.JoinCodes.Where(c => c.ExpiresUtc < cutoff).ExecuteDelete();
        db.SaveChanges();
        return code;
    }

    public IReadOnlyList<JoinCode> OpenJoinCodes(string ownerUserId)
    {
        using var db = _registry.Open();
        var now = DateTime.UtcNow;
        return db.JoinCodes.AsNoTracking().Where(c => c.OwnerUserId == ownerUserId && !c.UsedUtc.HasValue && c.ExpiresUtc > now).OrderByDescending(c => c.CreatedUtc).ToList();
    }

    private JoinCode? TakeJoinCode(string presented)
    {
        var normalized = presented.Replace("-", "").Trim().ToUpperInvariant();
        using var db = _registry.Open();
        var code = db.JoinCodes.AsNoTracking().FirstOrDefault(c => c.Code == normalized);
        return code is { UsedUtc: null } && code.ExpiresUtc > DateTime.UtcNow ? code : null;
    }

    private void MarkUsed(JoinCode code, string keyId)
    {
        using var db = _registry.Open();
        db.JoinCodes.Where(c => c.Code == code.Code).ExecuteUpdate(s => s.SetProperty(c => c.UsedUtc, DateTime.UtcNow).SetProperty(c => c.UsedByKeyId, keyId));
    }

    // 8 chars from an alphabet without look-alikes (no 0/O, 1/I): read aloud
    // over Discord, typed into the setup window.
    private static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(8);
        return new string([.. bytes.Select(b => alphabet[b % alphabet.Length])]);
    }

    private void Persist(AgentKeyRecord record)
    {
        using var db = _registry.Open();
        if (db.AgentKeys.AsNoTracking().Any(k => k.Id == record.Id)) db.AgentKeys.Update(record); else db.AgentKeys.Add(record);
        db.SaveChanges();
    }

    private List<AgentKeyRecord> ImportLegacyJson(RegistryDbContext db)
    {
        var path = Path.Combine(_registry.Root, "agents.json");
        if (!File.Exists(path)) return [];
        List<AgentKeyRecord> records;
        try
        {
            records = JsonSerializer.Deserialize<List<AgentKeyRecord>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception ex)
        {
            _log.LogError("agents.json is unreadable ({Message}) - nothing imported; fix or remove the file", ex.Message);
            return [];
        }
        foreach (var record in records.Where(r => r.KeyHash is { Length: > 0 }))
        {
            if (record.Id is not { Length: > 0 }) record.Id = Ids.New();
            db.AgentKeys.Add(record);
        }
        db.SaveChanges();
        db.ChangeTracker.Clear();
        File.Move(path, path + ".imported", overwrite: true);
        _log.LogInformation("Imported {Count} agent key(s) from agents.json into registry.db (file kept as agents.json.imported); they are unbound until an owner is assigned", records.Count);
        return records;
    }
}
