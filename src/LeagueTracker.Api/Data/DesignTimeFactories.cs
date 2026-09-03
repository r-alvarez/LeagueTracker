using LeagueTracker.Api.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LeagueTracker.Api.Data;

// `dotnet ef migrations add` builds the model from code and never opens the
// connection; without these it would run Program.cs to find a context, which
// boots the registry and every account against a real database.
public sealed class LeagueDbContextFactory : IDesignTimeDbContextFactory<LeagueDbContext>
{
    public LeagueDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LeagueDbContext>().UseNpgsql("Host=localhost;Database=design").Options);
}

public sealed class RegistryDbContextFactory : IDesignTimeDbContextFactory<RegistryDbContext>
{
    public RegistryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RegistryDbContext>().UseNpgsql("Host=localhost;Database=design").Options);
}
