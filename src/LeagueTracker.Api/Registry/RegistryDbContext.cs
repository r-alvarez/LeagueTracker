using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Registry;

// The one global store: users, the accounts they own, the machines they
// enrolled, and the short-lived codes/claims between them. Its own schema in
// the shared database; the per-account schemas hold nothing of this.
public sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserLogin> UserLogins => Set<UserLogin>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AgentKeyRecord> AgentKeys => Set<AgentKeyRecord>();
    public DbSet<JoinCode> JoinCodes => Set<JoinCode>();
    public DbSet<OwnershipClaim> OwnershipClaims => Set<OwnershipClaim>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<User>().HasMany(u => u.Logins).WithOne().HasForeignKey(l => l.UserId);
        b.Entity<UserLogin>().HasIndex(l => new { l.Issuer, l.Subject }).IsUnique();

        b.Entity<Account>().HasKey(a => a.Id);
        b.Entity<Account>().HasIndex(a => a.Slug).IsUnique();
        b.Entity<Account>().HasIndex(a => a.Puuid).IsUnique();
        b.Entity<Account>().HasIndex(a => a.OwnerUserId);

        b.Entity<AgentKeyRecord>().HasKey(k => k.Id);
        b.Entity<AgentKeyRecord>().HasIndex(k => k.KeyHash).IsUnique();
        b.Entity<AgentKeyRecord>().Property(k => k.Status).HasConversion<string>();
        b.Entity<AgentKeyRecord>().Property(k => k.Role).HasConversion<string>();
        b.Entity<JoinCode>().Property(c => c.Role).HasConversion<string>();
        b.Entity<OwnershipClaim>().Property(c => c.State).HasConversion<string>();
        b.Entity<OwnershipClaim>().HasIndex(c => c.AccountId);

        UtcDateTimes.Apply(b);
    }
}

// Where the registry lives (its schema, and the data root the SQLite era kept
// it in - still the parent of the account folders, the keys and agents.json)
// and that its schema is current before anything reads it.
public sealed class RegistryDatabase
{
    private const string LegacyFileName = "registry.db";

    private readonly DatabaseServer _server;

    public string Root { get; }
    public string LegacyPath { get; }
    public DbContextOptions<RegistryDbContext> Options { get; }

    public RegistryDatabase(DatabaseServer server, IOptions<AccountsOptions> accounts, IOptions<RiotOptions> riot, IWebHostEnvironment env)
    {
        _server = server;
        var configured = accounts.Value.DataRoot is { Length: > 0 } r ? r
            : accounts.Value.List.FirstOrDefault()?.DataDir is { Length: > 0 } first ? first
            : riot.Value.DataDir;
        Root = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        LegacyPath = Path.Combine(Root, LegacyFileName);
        Options = new DbContextOptionsBuilder<RegistryDbContext>().UseNpgsql(server.ForSchema(DatabaseServer.RegistrySchema)).Options;
    }

    public RegistryDbContext Open() => new(Options);

    // Boot: migrate, then bring the SQLite era's registry.db across once. A
    // failure here stops the boot on purpose - starting with an empty registry
    // would mint new account ids from configuration and orphan every schema.
    public void Migrate(ILogger log)
    {
        Directory.CreateDirectory(Root);
        _server.EnsureSchema(DatabaseServer.RegistrySchema);
        using var db = Open();
        db.Database.Migrate();
        SqliteImport.ImportIfPending(db, LegacyPath, "registry", log);
    }
}

public static class Ids
{
    // 12 hex chars from a GUID: unguessable enough for a URL-safe surrogate,
    // short enough to read in a log line.
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}
