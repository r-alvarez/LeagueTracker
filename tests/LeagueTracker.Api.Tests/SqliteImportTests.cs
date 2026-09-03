using System.Globalization;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Registry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeagueTracker.Api.Tests;

// The move off SQLite must lose nothing: every table, every column, every
// row, ids included - and refuse rather than drop what it does not know. The
// files are built in the SQLite era's exact schema (captured from real ones),
// every column filled from the row number, so a column added to the model
// later is covered without anyone listing it here.
[Collection(PostgresCollection.Name)]
public class SqliteImportTests(PostgresFixture postgres) : IDisposable
{
    private const int RowsPerTable = 3;
    private static readonly DateTime Epoch = new(2026, 5, 10, 7, 30, 13, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));
    private readonly DatabaseServer _server = postgres.NewServer();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    [Fact]
    public void Every_table_column_and_row_comes_across_and_the_file_is_retired()
    {
        using var db = AccountDb();
        var (file, keys) = SqliteFile(db, "sqlite-account-schema.sql", "leaguetracker.db");

        var result = SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance);

        Assert.NotNull(result);
        Assert.True(result.Verified);
        Assert.Equal(db.Model.GetEntityTypes().Count(), result.Tables.Count);
        Assert.All(result.Tables, t => Assert.Equal(RowsPerTable, t.SourceRows));
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".imported"));
        AssertEveryRowAsWritten(db, keys);
        Assert.Null(SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance));
    }

    [Fact]
    public void The_registry_comes_across_with_its_enums_and_optional_dates()
    {
        using var db = RegistryDb();
        var (file, keys) = SqliteFile(db, "sqlite-registry-schema.sql", "registry.db");

        var result = SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance);

        Assert.NotNull(result);
        Assert.True(result.Verified);
        AssertEveryRowAsWritten(db, keys);
        Assert.Equal([AgentRole.Recorder, AgentRole.Renderer, AgentRole.Recorder], db.AgentKeys.OrderBy(k => k.Id).Select(k => k.Role).ToList());
    }

    [Fact]
    public void Timestamps_keep_microseconds_and_come_back_as_utc()
    {
        using var db = AccountDb();
        var (file, _) = SqliteFile(db, "sqlite-account-schema.sql", "leaguetracker.db");
        SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance);

        var snapshot = db.LpSnapshots.Single(s => s.Id == 1);

        var written = (DateTime)Expected(db.Model.FindEntityType(typeof(LpSnapshot))!, db.Model.FindEntityType(typeof(LpSnapshot))!.FindProperty(nameof(LpSnapshot.TimestampUtc))!, 1, new Dictionary<IEntityType, List<object>>())!;
        Assert.NotEqual(0, written.Ticks % 10);
        Assert.Equal(written.Ticks / 10 * 10, snapshot.TimestampUtc.Ticks);
        Assert.Equal(DateTimeKind.Utc, snapshot.TimestampUtc.Kind);
    }

    [Fact]
    public void A_column_this_build_does_not_know_fails_the_import_and_leaves_the_schema_empty()
    {
        using var db = AccountDb();
        var (file, _) = SqliteFile(db, "sqlite-account-schema.sql", "leaguetracker.db", extraDdl: "ALTER TABLE \"Matches\" ADD COLUMN \"Mystery\" TEXT NULL");

        var ex = Assert.Throws<InvalidOperationException>(() => SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance));

        Assert.Contains("Mystery", ex.Message);
        Assert.True(SqliteImport.IsEmpty(db));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_table_this_build_does_not_know_fails_the_import()
    {
        using var db = AccountDb();
        var (file, _) = SqliteFile(db, "sqlite-account-schema.sql", "leaguetracker.db", extraDdl: "CREATE TABLE \"Legacy\" (\"Id\" INTEGER PRIMARY KEY)");

        var ex = Assert.Throws<InvalidOperationException>(() => SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance));

        Assert.Contains("Legacy", ex.Message);
        Assert.True(SqliteImport.IsEmpty(db));
    }

    [Fact]
    public void A_table_the_file_predates_imports_as_empty()
    {
        using var db = AccountDb();
        var (file, _) = SqliteFile(db, "sqlite-account-schema.sql", "leaguetracker.db", dropTables: ["ItemEvents"]);

        var result = SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance);

        Assert.NotNull(result);
        Assert.True(result.Verified);
        Assert.Equal(0, result.Tables.Single(t => t.Table == "ItemEvents").SourceRows);
        Assert.Equal(RowsPerTable, result.Tables.Single(t => t.Table == "Matches").SourceRows);
    }

    [Fact]
    public void A_schema_that_already_holds_data_leaves_a_file_alone()
    {
        using var db = AccountDb();
        var (file, _) = SqliteFile(db, "sqlite-account-schema.sql", "leaguetracker.db");
        SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance);
        File.Copy(file + ".imported", file);

        Assert.Null(SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance));
        Assert.True(File.Exists(file));
        Assert.Equal(RowsPerTable, db.Matches.Count());
    }

    [Fact]
    public void Identity_columns_continue_past_the_imported_ids()
    {
        using var db = AccountDb();
        var (file, _) = SqliteFile(db, "sqlite-account-schema.sql", "leaguetracker.db");
        SqliteImport.ImportIfPending(db, file, "test", NullLogger.Instance);

        var participant = new MatchParticipant { MatchId = "Matches-1" };
        db.Participants.Add(participant);
        db.SaveChanges();

        Assert.Equal(RowsPerTable + 1, participant.Id);
    }

    private LeagueDbContext AccountDb()
    {
        _server.EnsureSchema("acct_test");
        var db = new LeagueDbContext(new DbContextOptionsBuilder<LeagueDbContext>().UseNpgsql(_server.ForSchema("acct_test")).Options);
        db.Database.Migrate();
        return db;
    }

    private RegistryDbContext RegistryDb()
    {
        _server.EnsureSchema(DatabaseServer.RegistrySchema);
        var db = new RegistryDbContext(new DbContextOptionsBuilder<RegistryDbContext>().UseNpgsql(_server.ForSchema(DatabaseServer.RegistrySchema)).Options);
        db.Database.Migrate();
        return db;
    }

    private (string File, Dictionary<IEntityType, List<object>> Keys) SqliteFile(DbContext model, string schemaFixture, string name, string? extraDdl = null, string[]? dropTables = null)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, name);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ConnectionString);
        connection.Open();
        Execute(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", schemaFixture)));
        foreach (var table in dropTables ?? []) Execute(connection, $"DROP TABLE \"{table}\"");
        if (extraDdl is not null) Execute(connection, extraDdl);

        var keys = new Dictionary<IEntityType, List<object>>();
        foreach (var entity in SqliteImport.InsertionOrder(model.Model))
        {
            if (dropTables?.Contains(entity.GetTableName()) is true) continue;
            keys[entity] = [];
            var columns = entity.GetProperties().ToList();
            for (var i = 1; i <= RowsPerTable; i++)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = $"INSERT INTO \"{entity.GetTableName()}\" ({string.Join(", ", columns.Select(c => $"\"{c.GetColumnName()}\""))}) VALUES ({string.Join(", ", columns.Select((_, n) => $"@p{n}"))})";
                for (var n = 0; n < columns.Count; n++) insert.Parameters.AddWithValue($"@p{n}", SqliteValue(Expected(entity, columns[n], i, keys)) ?? DBNull.Value);
                insert.ExecuteNonQuery();
                keys[entity].Add(Expected(entity, entity.FindPrimaryKey()!.Properties[0], i, keys)!);
            }
        }
        return (file, keys);
    }

    private static void AssertEveryRowAsWritten(DbContext db, Dictionary<IEntityType, List<object>> keys)
    {
        foreach (var (entity, ids) in keys)
        {
            for (var i = 1; i <= RowsPerTable; i++)
            {
                var row = db.Find(entity.ClrType, ids[i - 1]);
                Assert.NotNull(row);
                foreach (var property in entity.GetProperties())
                {
                    var actual = property.PropertyInfo!.GetValue(row);
                    Assert.Equal(AfterImport(Expected(entity, property, i, keys)), actual);
                    if (actual is DateTime d) Assert.Equal(DateTimeKind.Utc, d.Kind);
                }
            }
        }
    }

    // What row i holds in a column: shaped by the CLR type and the row number,
    // null on every third row of a nullable column, one of the parent's ids
    // for a foreign key.
    private static object? Expected(IEntityType entity, IProperty property, int i, IReadOnlyDictionary<IEntityType, List<object>> keys)
    {
        if (property.IsForeignKey())
        {
            var parents = keys[property.GetContainingForeignKeys().First().PrincipalEntityType];
            return parents[(i - 1) % parents.Count];
        }
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (property.IsPrimaryKey()) return type == typeof(string) ? $"{entity.GetTableName()}-{i}" : Convert.ChangeType(i, type, CultureInfo.InvariantCulture);
        if (property.IsNullable && i % 3 == 0) return null;
        if (type == typeof(string)) return $"{property.Name} #{i} \\ \"quoted\"  tab\t new\nline ünïcödé";
        if (type == typeof(bool)) return i % 2 == 0;
        if (type == typeof(int) || type == typeof(long)) return Convert.ChangeType(i * 7, type, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return i + 0.125;
        if (type == typeof(DateTime)) return Epoch.AddTicks(i * 1234567L + 1);
        if (type.IsEnum) return Enum.GetValues(type).GetValue((i - 1) % Enum.GetValues(type).Length);
        throw new NotSupportedException($"{entity.GetTableName()}.{property.Name}: {type.Name}");
    }

    // How EF Core's SQLite provider stored each kind of value.
    private static object? SqliteValue(object? value) => value switch
    {
        null => null,
        bool b => b ? 1L : 0L,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        int n => (long)n,
        _ => value,
    };

    // The one accepted change: Postgres keeps microseconds, not 100 ns ticks.
    private static object? AfterImport(object? value) =>
        value is DateTime d ? new DateTime(d.Ticks / 10 * 10, DateTimeKind.Utc) : value;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
