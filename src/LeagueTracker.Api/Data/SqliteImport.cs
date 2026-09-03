using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LeagueTracker.Api.Data;

// Brings a SQLite-era file into this context's (empty) schema once, and proves
// it did before the file is retired. Ranks at capture time, LP snapshots, the
// poller's memory and the puuid cache exist nowhere but in that file, so this
// is a row-for-row copy, not a re-import of the raw games: every table in the
// file must be one the model knows and every column a mapped property (an
// unknown one fails the import rather than being dropped), ids are kept, and
// both sides are re-read through the same canonical form and hashed. All of
// it is one transaction, so a failure leaves an empty schema for the retry.
public static class SqliteImport
{
    private const int Batch = 5000;
    private static readonly string EmptyHash = Hash([]);

    public sealed record TableResult(string Table, long SourceRows, long TargetRows, string SourceHash, string TargetHash)
    {
        public bool Matches => SourceRows == TargetRows && SourceHash == TargetHash;
    }

    public sealed record Result(string File, IReadOnlyList<TableResult> Tables)
    {
        public bool Verified => Tables.All(t => t.Matches);
        public long Rows => Tables.Sum(t => t.SourceRows);
    }

    // Null when there is nothing to do: no file, or a schema that already holds
    // data (an earlier import whose rename failed, or a hand copy) - never a
    // case to guess at, so it is logged and left alone.
    public static Result? ImportIfPending(DbContext db, string file, string label, ILogger log)
    {
        if (!File.Exists(file)) return null;
        if (!IsEmpty(db))
        {
            log.LogWarning("{Label}: {File} is still beside a schema that already holds data - left alone; rename it to {File}.imported once you are sure it was imported", label, file, file);
            return null;
        }
        var result = Import(db, file, log);
        Retire(file, log);
        return result;
    }

    // EF1002 at the two raw statements here: the identifiers are the model's own
    // table and column names, never input, and identifiers cannot be parameters.
#pragma warning disable EF1002
    public static bool IsEmpty(DbContext db) =>
        db.Model.GetEntityTypes().All(entity =>
            !db.Database.SqlQueryRaw<bool>($"SELECT EXISTS (SELECT 1 FROM \"{entity.GetTableName()}\") AS \"Value\"").Single());

    public static Result Import(DbContext db, string file, ILogger log)
    {
        var entities = InsertionOrder(db.Model);
        // No pooling: the file has to be closed for real before it is renamed.
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        source.Open();
        var sourceTables = SourceTables(source);
        var unknownTables = sourceTables.Except(entities.Select(e => e.GetTableName()!), StringComparer.OrdinalIgnoreCase).ToList();
        if (unknownTables is { Count: > 0 })
        {
            throw new InvalidOperationException($"{file} holds table(s) this build does not know ({string.Join(", ", unknownTables)}) - nothing imported");
        }

        List<TableResult> results = [];
        var tracking = (db.ChangeTracker.AutoDetectChangesEnabled, db.ChangeTracker.QueryTrackingBehavior);
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        using var transaction = db.Database.BeginTransaction();
        try
        {
            foreach (var entity in entities)
            {
                var table = entity.GetTableName()!;
                if (!sourceTables.Contains(table, StringComparer.OrdinalIgnoreCase))
                {
                    log.LogInformation("{File}: no {Table} table (the file predates it) - empty", file, table);
                    results.Add(new TableResult(table, 0, 0, EmptyHash, EmptyHash));
                    continue;
                }
                var sourceHash = CopyTable(db, source, entity, file, log, out var sourceRows);
                ResetIdentity(db, entity);
                var targetHash = HashTarget(db, entity, out var targetRows);
                results.Add(new TableResult(table, sourceRows, targetRows, sourceHash, targetHash));
            }
            var result = new Result(file, results);
            if (!result.Verified)
            {
                var mismatches = results.Where(t => !t.Matches)
                    .Select(t => $"{t.Table}: {t.SourceRows} rows/{t.SourceHash[..12]} in the file vs {t.TargetRows} rows/{t.TargetHash[..12]} imported");
                throw new InvalidOperationException($"{file}: verification failed, rolled back - {string.Join("; ", mismatches)}");
            }
            transaction.Commit();
            log.LogInformation("{File}: imported and verified - {Rows} rows: {Detail}", file, result.Rows, string.Join(", ", results.Select(t => $"{t.Table} {t.SourceRows}")));
            return result;
        }
        finally
        {
            db.ChangeTracker.Clear();
            (db.ChangeTracker.AutoDetectChangesEnabled, db.ChangeTracker.QueryTrackingBehavior) = tracking;
        }
    }

    private static string CopyTable(DbContext db, SqliteConnection source, IEntityType entity, string file, ILogger log, out long rows)
    {
        var table = entity.GetTableName()!;
        var properties = entity.GetProperties().ToList();
        var sourceColumns = SourceColumns(source, table);
        var unknownColumns = sourceColumns.Except(properties.Select(p => p.GetColumnName()), StringComparer.OrdinalIgnoreCase).ToList();
        if (unknownColumns is { Count: > 0 })
        {
            throw new InvalidOperationException($"{file}: {table} has column(s) this build does not know ({string.Join(", ", unknownColumns)}) - nothing imported");
        }
        var present = properties.Where(p => sourceColumns.Contains(p.GetColumnName(), StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var missing in properties.Except(present))
        {
            log.LogInformation("{File}: {Table}.{Column} is not in the file (the file predates it) - default value", file, table, missing.GetColumnName());
        }

        using var command = source.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", present.Select(p => $"\"{p.GetColumnName()}\""))} FROM \"{table}\"";
        using var reader = command.ExecuteReader();
        List<string> canonical = [];
        var pending = new List<object>(Batch);
        while (reader.Read())
        {
            var instance = Activator.CreateInstance(entity.ClrType)!;
            for (var i = 0; i < present.Count; i++)
            {
                Setter(present[i]).SetValue(instance, FromSqlite(reader.IsDBNull(i) ? null : reader.GetValue(i), present[i]));
            }
            canonical.Add(Canonical(instance, properties));
            pending.Add(instance);
            if (pending.Count == Batch) Flush(db, pending);
        }
        Flush(db, pending);
        rows = canonical.Count;
        return Hash(canonical);
    }

    private static void Flush(DbContext db, List<object> pending)
    {
        if (pending is not { Count: > 0 }) return;
        db.AddRange(pending);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        pending.Clear();
    }

    // Explicit ids went in; the identity sequence must continue past them.
    private static void ResetIdentity(DbContext db, IEntityType entity)
    {
        var table = entity.GetTableName()!;
        var generated = entity.FindPrimaryKey()!.Properties
            .Where(p => p.ValueGenerated == ValueGenerated.OnAdd && (p.ClrType == typeof(long) || p.ClrType == typeof(int)));
        foreach (var key in generated)
        {
            var column = key.GetColumnName();
            db.Database.ExecuteSqlRaw(
                $"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{column}'), COALESCE((SELECT MAX(\"{column}\") FROM \"{table}\"), 1), (SELECT MAX(\"{column}\") FROM \"{table}\") IS NOT NULL)");
        }
    }
#pragma warning restore EF1002

    private static string HashTarget(DbContext db, IEntityType entity, out long rows)
    {
        var properties = entity.GetProperties().ToList();
        var set = (IEnumerable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(entity.ClrType).Invoke(db, null)!;
        List<string> canonical = [];
        foreach (var instance in set) canonical.Add(Canonical(instance, properties));
        rows = canonical.Count;
        return Hash(canonical);
    }

    private static object? FromSqlite(object? raw, IProperty property)
    {
        if (raw is null) return null;
        // Enums travelled as their names (HasConversion<string>); the converter
        // that reads them back lives on the type mapping, not on the property.
        if (property.FindTypeMapping()?.Converter is { } converter && converter.ProviderClrType == typeof(string) && raw is string text) return converter.ConvertFromProvider(text);
        var target = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (target == typeof(DateTime)) return DateTime.Parse((string)raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (target == typeof(bool)) return Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0;
        if (target == typeof(int)) return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        if (target == typeof(long)) return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        if (target == typeof(double)) return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        if (target == typeof(string)) return raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
        throw new NotSupportedException($"{property.DeclaringType.DisplayName()}.{property.Name}: no SQLite reading for {target.Name}");
    }

    private static string Canonical(object instance, IReadOnlyList<IProperty> properties)
    {
        var row = new StringBuilder();
        foreach (var property in properties) row.Append(CanonicalValue(Setter(property).GetValue(instance))).Append('\u001f');
        return row.ToString();
    }

    // Timestamps compare at microseconds: Postgres keeps nothing finer, and
    // SQLite kept the full tick.
    private static string CanonicalValue(object? value) => value switch
    {
        null => "\u2400",
        string s => s.Replace("\\", "\\\\").Replace("\u001f", "\\u001f"),
        bool b => b ? "1" : "0",
        DateTime d => (d.Ticks / 10).ToString(CultureInfo.InvariantCulture),
        double x => x.ToString("R", CultureInfo.InvariantCulture),
        int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        Enum e => e.ToString(),
        _ => throw new NotSupportedException($"no canonical form for {value.GetType().Name}"),
    };

    private static string Hash(List<string> rows)
    {
        rows.Sort(StringComparer.Ordinal);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(row));
            sha.AppendData("\n"u8);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static System.Reflection.PropertyInfo Setter(IProperty property) =>
        property.PropertyInfo ?? throw new NotSupportedException($"{property.DeclaringType.DisplayName()}.{property.Name} is a shadow property");

    // Parents before children, so every foreign key already resolves.
    internal static List<IEntityType> InsertionOrder(IModel model)
    {
        List<IEntityType> ordered = [];
        var remaining = model.GetEntityTypes().ToList();
        while (remaining is { Count: > 0 })
        {
            var ready = remaining.Where(e => e.GetForeignKeys().All(fk => fk.PrincipalEntityType == e || ordered.Contains(fk.PrincipalEntityType))).ToList();
            if (ready is []) throw new InvalidOperationException("The model's foreign keys form a cycle");
            ordered.AddRange(ready);
            remaining.RemoveAll(ready.Contains);
        }
        return ordered;
    }

    private static List<string> SourceTables(SqliteConnection source)
    {
        using var command = source.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        using var reader = command.ExecuteReader();
        List<string> tables = [];
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static List<string> SourceColumns(SqliteConnection source, string table)
    {
        using var command = source.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        List<string> columns = [];
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    // Kept, never deleted: the `.imported` suffix is the marker the next boot
    // reads, and the -wal/-shm go with the file they belong to.
    private static void Retire(string file, ILogger log)
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists))
        {
            try
            {
                File.Move(path, path + ".imported");
            }
            catch (Exception ex)
            {
                log.LogWarning("{File}: imported, but could not be renamed ({Message}) - rename it to .imported by hand", path, ex.Message);
            }
        }
    }
}
