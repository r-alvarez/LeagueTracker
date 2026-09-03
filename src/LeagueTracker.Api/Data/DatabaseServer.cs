using System.Text.RegularExpressions;
using LeagueTracker.Api.Accounts;
using Npgsql;

namespace LeagueTracker.Api.Data;

// The one PostgreSQL database and how it is carved up: the registry in its
// own schema, every tracked account in its own - the account is bound to its
// schema through the connection's search path the way it used to be bound to
// its folder's SQLite through the path. Schema names come from the registry's
// surrogate ids (never the Riot ID, which can be renamed).
public sealed partial class DatabaseServer(IConfiguration configuration)
{
    public const string ConnectionName = "LeagueTracker";
    public const string RegistrySchema = "registry";
    public const string SchemaBytesSql =
        "SELECT COALESCE(SUM(pg_total_relation_size(c.oid)), 0)::bigint AS \"Value\" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = current_schema() AND c.relkind = 'r'";

    public string ConnectionString { get; } = configuration.GetConnectionString(ConnectionName)
        ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionName} is not set (Host=...;Database=...;Username=...;Password=...)");

    public static string AccountSchema(Account account) =>
        SchemaSafeId().IsMatch(account.Id)
            ? $"acct_{account.Id}"
            : throw new InvalidOperationException($"Account id '{account.Id}' cannot name a schema");

    public string ForSchema(string schema) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = schema }.ConnectionString;

    // Before Migrate(): a search path naming a schema that does not exist makes
    // every CREATE TABLE fail with "no schema has been selected to create in".
    public void EnsureSchema(string schema)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"";
        command.ExecuteNonQuery();
    }

    [GeneratedRegex("^[a-z0-9]{1,40}$")]
    private static partial Regex SchemaSafeId();
}
