using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Data;

public sealed class LeagueDbContext(DbContextOptions<LeagueDbContext> options) : DbContext(options)
{
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchParticipant> Participants => Set<MatchParticipant>();
    public DbSet<Death> Deaths => Set<Death>();
    public DbSet<DeathDamage> DeathDamages => Set<DeathDamage>();
    public DbSet<PositionSample> PositionSamples => Set<PositionSample>();
    public DbSet<KillEvent> KillEvents => Set<KillEvent>();
    public DbSet<ObjectiveEvent> ObjectiveEvents => Set<ObjectiveEvent>();
    public DbSet<ItemEvent> ItemEvents => Set<ItemEvent>();
    public DbSet<LpSnapshot> LpSnapshots => Set<LpSnapshot>();
    public DbSet<KnownMatch> KnownMatches => Set<KnownMatch>();
    public DbSet<KeyValue> KeyValues => Set<KeyValue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Match>().HasMany(m => m.Participants).WithOne().HasForeignKey(p => p.MatchId);
        modelBuilder.Entity<Match>().HasMany(m => m.DeathEvents).WithOne().HasForeignKey(d => d.MatchId);
        modelBuilder.Entity<Match>().HasMany(m => m.PositionSamples).WithOne().HasForeignKey(p => p.MatchId);
        modelBuilder.Entity<Match>().HasMany(m => m.KillEvents).WithOne().HasForeignKey(k => k.MatchId);
        modelBuilder.Entity<Match>().HasMany(m => m.ObjectiveEvents).WithOne().HasForeignKey(o => o.MatchId);
        modelBuilder.Entity<Match>().HasMany(m => m.ItemEvents).WithOne().HasForeignKey(i => i.MatchId);
        modelBuilder.Entity<Death>().HasMany(d => d.DamageInstances).WithOne().HasForeignKey(dd => dd.DeathId);
        modelBuilder.Entity<Match>().HasIndex(m => m.GameEndUtc);
        modelBuilder.Entity<MatchParticipant>().HasIndex(p => p.MatchId);
        modelBuilder.Entity<Death>().HasIndex(d => d.MatchId);
        modelBuilder.Entity<PositionSample>().HasIndex(p => new { p.MatchId, p.TimeSec });
        modelBuilder.Entity<KillEvent>().HasIndex(k => k.MatchId);
        modelBuilder.Entity<ObjectiveEvent>().HasIndex(o => o.MatchId);
        modelBuilder.Entity<ItemEvent>().HasIndex(i => i.MatchId);
        modelBuilder.Entity<LpSnapshot>().HasIndex(s => new { s.Queue, s.TimestampUtc });
    }
}
