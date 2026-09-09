using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Tests;

// The ~95-account wall was one Npgsql pool per schema; these pin the shape
// that replaced it - one pool, the schema bound per connection open.
[Collection(PostgresCollection.Name)]
public class DatabaseServerTests(PostgresFixture postgres) : IDisposable
{
    private readonly DatabaseServer _server = postgres.NewServer();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Each_context_sees_its_own_schema_and_they_share_one_physical_connection()
    {
        _server.EnsureSchema("acct_one");
        _server.EnsureSchema("acct_two");

        for (var round = 0; round < 3; round++)
        {
            Assert.Equal("acct_one", await CurrentSchemaAsync("acct_one"));
            Assert.Equal("acct_two", await CurrentSchemaAsync("acct_two"));
        }

        using var db = new LeagueDbContext(_server.OptionsFor<LeagueDbContext>("acct_two"));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database()").SingleAsync());
    }

    [Fact]
    public async Task Migrations_land_in_the_bound_schema()
    {
        _server.EnsureSchema("acct_mig");
        using var db = new LeagueDbContext(_server.OptionsFor<LeagueDbContext>("acct_mig"));
        await db.Database.MigrateAsync();

        Assert.True(await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'acct_mig' AND tablename = 'Matches') AS \"Value\"").SingleAsync());
    }

    [Theory]
    [InlineData("public")]
    [InlineData("acct_x\"; DROP SCHEMA registry; --")]
    public void Only_the_schemas_this_server_hands_out_can_be_bound(string schema) =>
        Assert.Throws<InvalidOperationException>(() => _server.OptionsFor<LeagueDbContext>(schema));

    private async Task<string> CurrentSchemaAsync(string schema)
    {
        using var db = new LeagueDbContext(_server.OptionsFor<LeagueDbContext>(schema));
        return await db.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync();
    }
}
