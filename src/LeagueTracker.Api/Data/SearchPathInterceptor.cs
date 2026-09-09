using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LeagueTracker.Api.Data;

// Binds a context to its schema on every open rather than in the connection
// string, so all schemas share one pool. Npgsql runs DISCARD ALL when a
// connection goes back to the pool, so each checkout starts on the role's
// default path: a context that somehow skipped this finds no tables at all,
// never another account's.
public sealed class SearchPathInterceptor(string schema) : DbConnectionInterceptor
{
    private readonly string _sql = $"SET search_path TO \"{DatabaseServer.SchemaName(schema)}\"";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _sql;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = _sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
