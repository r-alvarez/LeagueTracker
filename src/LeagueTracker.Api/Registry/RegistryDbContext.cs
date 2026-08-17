using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Registry;

// The one global database: users, the accounts they own, the machines they
// enrolled, and the short-lived codes/claims between them. Lives next to the
// per-account folders (<DataRoot>/registry.db). Per-account SQLites stay
// untouched - this replaces accounts.json and agents.json, nothing else.
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
    }
}

// Where registry.db lives and that it exists with the current schema before
// anything reads it. Same PRAGMA-driven upgrade rule as the account databases:
// only the ALTERs a table lacks, a current db runs nothing.
public sealed class RegistryDatabase
{
    private static readonly (string Table, string Column, string Definition)[] Upgrades = [];

    public string Path { get; }
    public string Root { get; }

    public RegistryDatabase(IOptions<AccountsOptions> accounts, IOptions<RiotOptions> riot, IWebHostEnvironment env)
    {
        var configured = accounts.Value.DataRoot is { Length: > 0 } r ? r
            : accounts.Value.List.FirstOrDefault()?.DataDir is { Length: > 0 } first ? first
            : riot.Value.DataDir;
        Root = System.IO.Path.IsPathRooted(configured) ? configured : System.IO.Path.Combine(env.ContentRootPath, configured);
        Path = System.IO.Path.Combine(Root, "registry.db");
    }

    public DbContextOptions<RegistryDbContext> Options =>
        new DbContextOptionsBuilder<RegistryDbContext>().UseSqlite($"Data Source={Path}").Options;

    public RegistryDbContext Open() => new(Options);

    public void EnsureCreated(ILogger log)
    {
        Directory.CreateDirectory(Root);
        using var db = Open();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL");
        var connection = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            foreach (var (table, column, definition) in Upgrades)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = $"PRAGMA table_info({table})";
                using var reader = pragma.ExecuteReader();
                var present = false;
                while (reader.Read()) present |= string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase);
                if (present) continue;
                db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
                log.LogInformation("registry.db: added column {Table}.{Column}", table, column);
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }
}

public static class Ids
{
    // 12 hex chars from a GUID: unguessable enough for a URL-safe surrogate,
    // short enough to read in a log line.
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}
