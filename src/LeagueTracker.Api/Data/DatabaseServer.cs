using System.Text.RegularExpressions;
using LeagueTracker.Api.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LeagueTracker.Api.Data;

// The one PostgreSQL database and how it is carved up: the registry in its
// own schema, every tracked account in its own - the account is bound to its
// schema through the connection's search path the way it used to be bound to
// its folder's SQLite through the path. Schema names come from the registry's
// surrogate ids (never the Riot ID, which can be renamed).
public sealed partial class DatabaseServer : IDisposable
{
    public const string ConnectionName = "LeagueTracker";
    public const string RegistrySchema = "registry";
    public const string SchemaBytesSql =
        "SELECT COALESCE(SUM(pg_total_relation_size(c.oid)), 0)::bigint AS \"Value\" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = current_schema() AND c.relkind = 'r'";

    // One pool for the whole process. Npgsql keys pools by connection string,
    // so a `Search Path=` per schema was a pool per account - and at ~95
    // accounts the warm connection each pool kept exhausted max_connections.
    public NpgsqlDataSource DataSource { get; }

    public DatabaseServer(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionName} is not set (Host=...;Database=...;Username=...;Password=...)");
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    public static string AccountSchema(Account account) =>
        SchemaSafeId().IsMatch(account.Id)
            ? $"acct_{account.Id}"
            : throw new InvalidOperationException($"Account id '{account.Id}' cannot name a schema");

    public void Configure(DbContextOptionsBuilder options, string schema) =>
        options.UseNpgsql(DataSource).AddInterceptors(new SearchPathInterceptor(schema));

    public DbContextOptions<T> OptionsFor<T>(string schema) where T : DbContext
    {
        var options = new DbContextOptionsBuilder<T>();
        Configure(options, schema);
        return options.Options;
    }

    // Before Migrate(): a search path naming a schema that does not exist makes
    // every CREATE TABLE fail with "no schema has been selected to create in".
    public void EnsureSchema(string schema)
    {
        using var connection = DataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{SchemaName(schema)}\"";
        command.ExecuteNonQuery();
    }

    public void Dispose() => DataSource.Dispose();

    internal static string SchemaName(string schema) =>
        schema == RegistrySchema || SchemaSafeName().IsMatch(schema)
            ? schema
            : throw new InvalidOperationException($"'{schema}' is not a schema this server hands out");

    [GeneratedRegex("^[a-z0-9]{1,40}$")]
    private static partial Regex SchemaSafeId();

    [GeneratedRegex("^acct_[a-z0-9]{1,40}$")]
    private static partial Regex SchemaSafeName();
}
