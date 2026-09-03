using LeagueTracker.Api.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LeagueTracker.Api.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

// One Postgres container for the whole run; every test gets its own database
// in it, because the schema names inside (registry, acct_<id>) are fixed.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public DatabaseServer NewServer()
    {
        var name = "t_" + Guid.NewGuid().ToString("N")[..12];
        using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            create.ExecuteNonQuery();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>($"ConnectionStrings:{DatabaseServer.ConnectionName}", connectionString)])
            .Build();
        return new DatabaseServer(configuration);
    }
}
