# ops/postgres — SQLite to PostgreSQL

Branch: `ops/postgres`, from `main` at a81fa32 (2026-09-03).

Goal: the tracker runs on PostgreSQL in production instead of one SQLite file
per account plus `registry.db`, and the move loses nothing: every row of every
table of every account and of the registry comes across and is proven to.

## Decisions

### One database, one schema per account, a `registry` schema

Alternatives: (a) an `AccountId` column on every per-account table; (b) a
schema per account; (c) a database per account.

Chosen (b). The entity model, the 21 files that query `LeagueDbContext` and
the per-account failure isolation (`AccountInitializer`) stay exactly as they
are: an account is bound to its schema through the connection's search path,
the way it was bound to its file through the path. (a) would turn
`Match.Id` (Riot's match id, shared by two tracked accounts in the same game)
into a composite key, cascade that through every foreign key and every join,
and put a leak-by-omission risk on every query. (c) multiplies the ops
surface for nothing at three accounts.

Trade-off accepted: migrations run once per schema at boot (a loop over the
accounts, still inside the initializer's retry/isolation), and an account
count in the thousands would want (a). Not this deployment.

Schema names: `registry`, and `acct_<Account.Id>` (the 12-hex surrogate the
registry mints — never the Riot ID, which can be renamed).

### PostgreSQL only at runtime

`Microsoft.EntityFrameworkCore.Sqlite` goes; `Microsoft.Data.Sqlite` stays
only as the reader inside the one-shot importer. Alternative: keep both
providers with a switch. Rejected: EF migrations are provider-specific, so
every schema change would be written and tested twice, and a dev/prod
provider split is how SQLite-only bugs (`s.True`, unquoted identifiers) got
in before. Local development and the tests run a real Postgres in Docker.

### EF migrations replace EnsureCreated + the ALTER lists

`AccountInitializer.Upgrades` and `RegistryDatabase.Upgrades` (PRAGMA-driven
column adds) are retired. Both contexts get a migrations folder and
`Migrate()` runs per schema. The schema is created (`CREATE SCHEMA IF NOT
EXISTS`) on the base connection first, because a search path naming a schema
that does not exist makes `CREATE TABLE` fail with "no schema has been
selected".

### Timestamps: UTC on both sides, microsecond precision

Npgsql refuses a `DateTime` with Kind Local/Unspecified for `timestamptz`.
Every timestamp column in the app is UTC by name (`*Utc`), so both contexts
apply one converter: Local is converted, Unspecified is stamped UTC, reads
come back Kind Utc. The registry already had the read half.

Postgres stores microseconds; SQLite stored the full 100 ns tick. Riot's
timestamps are millisecond-precise so nothing changes for them; the few
`DateTime.UtcNow` captures (LP snapshots, registry rows) lose their last
digit. The verification hashes at microsecond precision on both sides so
this is the one, known, accepted difference.

### Lossless import, in one transaction, verified before the file is retired

On first boot (and on demand for rehearsal) each schema that is empty and has
a SQLite file next to it gets that file's contents:

- the file is opened read-only (`Mode=ReadOnly`; a leftover `-wal` is still
  read, so a container killed mid-write loses nothing);
- every table in the file must map to an entity and every column to a mapped
  property — an unknown table or column fails the import instead of being
  silently dropped;
- rows are copied with their ids (identity columns accept explicit values),
  then the sequences are reset past the highest id;
- verification re-reads both sides through the same canonicalisation over
  the EF model's properties, sorts ordinally and hashes per table; counts and
  hashes must match for every table;
- all of it runs in one transaction: a failure or mismatch rolls back to an
  empty schema, the account is reported unavailable with the reason and the
  initializer retries after 60 s (as it does for a locked file today);
- only after commit is the file renamed `leaguetracker.db.imported` (the
  `accounts.json.imported` convention) — nothing is ever deleted.

An empty schema without a file is a genuinely new account. A non-empty schema
with a file still present is left alone and logged: somebody already imported
or the rename failed; either way not a case to guess at.

Rollback of the whole move: redeploy the previous image; the SQLite files are
never written.

### Versions

PostgreSQL 18 (`postgres:18-alpine`; 18.6 current, 19 not released),
`Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 (11 is preview-only, and 10.x
pairs with EF Core 10 / .NET 10), `Testcontainers.PostgreSql` 4.14.0.

### Rehearsal on real data (2026-09-03)

Consistent copies (python's `sqlite3` backup API) of the live
`data/leaguetracker.db` (527 matches, 252,419 rows over 11 tables) and
`data/registry.db` (11 rows) went through the first-boot path into a
scratch database on a local `postgres:18-alpine`: both imported and
verified, files renamed, identity sequences continue past the highest id,
every read endpoint answers 200. A JSON diff of the same endpoints against
a SQLite instance of main showed exactly three kinds of difference:

- UTC timestamps now carry their `Z`. The SQLite provider handed
  `DateTime`s back as Kind Unspecified, so the SPA's `new Date(...)` parsed
  them as local time and showed match times an hour off during BST. The
  registry context already had the read-side fix for exactly that reason
  ("an expiry an hour off is a claim that looks dead"); the account context
  did not. A bug fix, disclosed here because match times on the site move
  by the local offset.
- LP snapshot timestamps at microseconds (the accepted precision change).
- `hasReplay`/`challengesAsOfUtc`: environmental (a scratch folder).

### Gotchas met

- `dotnet ef migrations add` builds before it writes the migration, so an
  app run with `--no-build` right after it carries the previous
  migration set: `Migrate()` created only `__EFMigrationsHistory`.
- `HasConversion<string>()` on an enum leaves `IProperty.GetValueConverter()`
  null; the converter is on `FindTypeMapping()`.
- `Microsoft.Data.Sqlite` 10.0.9 pins a SQLitePCLRaw with a known CVE
  (NU1903 is an error here); the `bundle_e_sqlite3` 3.0.3 override stays.
- The Npgsql provider floats EF Core 10.0.4 while `Design` (private assets)
  built the API against 10.0.9; the test project then hit MSB3277. EF Core
  and Relational are referenced explicitly at 10.0.9.
- With `Search Path` on the connection string, `Migrate()` creates
  `__EFMigrationsHistory` in that schema and finds it again on the next
  boot - verified on a second start (no migration re-applied).

### Follow-ups deliberately not in this branch

JSON columns to `jsonb`, dropping the dead `Hosts` column, snake_case naming,
query tuning. The move keeps the shape identical so the verification is
about data only.
