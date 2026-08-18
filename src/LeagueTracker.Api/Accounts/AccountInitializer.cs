using System.Collections.Concurrent;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Accounts;

// Creates/upgrades an account's SQLite and remembers whether that worked.
// One account's broken db (locked, read-only, torn after an unclean NAS
// unmount) must not take the other accounts down with it: it is marked
// unavailable, its own API answers 503 with the reason, the poller skips
// it, and the next request or poll pass after RetryEvery tries again - so
// a transient lock heals itself without a restart.
public sealed class AccountInitializer(AccountScopes scopes, ILogger<AccountInitializer> log)
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(60);

    // Additive columns land as ALTERs for exactly the columns a table lacks -
    // checked against PRAGMA table_info first, so an already-current db runs
    // no statement at all and a real error surfaces instead of being mistaken
    // for "column already exists". EnsureCreated never alters an existing
    // table, and wiping the db would cost capture-time-only data (ranks, LP).
    private static readonly (string Table, string Column, string Definition)[] Upgrades =
    [
        ("Matches", "AllyJungler", "TEXT NULL"),
        ("Matches", "TotalTimeSpentDead", "INTEGER NOT NULL DEFAULT 0"),
        ("Matches", "LongestTimeSpentLiving", "INTEGER NOT NULL DEFAULT 0"),
        ("Matches", "TotalTimeCcDealt", "INTEGER NOT NULL DEFAULT 0"),
        ("Matches", "ChallengesJson", "TEXT NOT NULL DEFAULT ''"),
        ("Matches", "AvgUnspentGold", "INTEGER NULL"),
        ("Matches", "MaxUnspentGold", "INTEGER NULL"),
        ("Matches", "FirstWardSec", "INTEGER NULL"),
        ("Matches", "FirstControlWardSec", "INTEGER NULL"),
        ("Matches", "WardsFirst10", "INTEGER NOT NULL DEFAULT 0"),
        ("Matches", "Level6LeadSec", "INTEGER NULL"),
        ("Matches", "Level11LeadSec", "INTEGER NULL"),
        ("Matches", "Level16LeadSec", "INTEGER NULL"),
        ("Matches", "FriendlyEpicObjectives", "INTEGER NOT NULL DEFAULT 0"),
        ("Matches", "ObjectivesPresentFor", "INTEGER NOT NULL DEFAULT 0"),
        ("Matches", "FightsJson", "TEXT NOT NULL DEFAULT ''"),
        ("Matches", "TeamGoldDiff15", "INTEGER NULL"),
        ("Matches", "TeamGoldDiff20", "INTEGER NULL"),
        ("Matches", "ContestedEpicsTaken", "INTEGER NOT NULL DEFAULT 0"),
        ("Deaths", "EnemyJunglerNear", "INTEGER NULL"),
        ("KillEvents", "AssistIds", "TEXT NOT NULL DEFAULT ''"),
    ];

    private sealed class State
    {
        public bool Ready;
        public string? Error;
        public DateTime LastAttemptUtc;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public bool IsReady(Account account) => _states.TryGetValue(account.Slug, out var s) && s.Ready;

    // The reason the account is unavailable; null when it is fine (or untried).
    public string? ErrorFor(Account account) => _states.TryGetValue(account.Slug, out var s) && !s.Ready ? s.Error : null;

    // True when the account can serve. A failed account is retried at most
    // every RetryEvery, whoever asks (a request, the poller, the boot loop).
    public bool EnsureReady(Account account)
    {
        var state = _states.GetOrAdd(account.Slug, _ => new State());
        lock (state)
        {
            if (state.Ready) return true;
            if (state.Error is not null && DateTime.UtcNow - state.LastAttemptUtc < RetryEvery) return false;
            state.LastAttemptUtc = DateTime.UtcNow;
            try
            {
                Initialize(account);
                if (state.Error is not null) log.LogInformation("Account {Slug}: database available again", account.Slug);
                state.Ready = true;
                state.Error = null;
                return true;
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                if (message != state.Error)
                {
                    log.LogError(ex, "Account {Slug}: database initialisation failed - unavailable until it succeeds (retried every {Seconds}s)", account.Slug, RetryEvery.TotalSeconds);
                }
                state.Error = message;
                return false;
            }
        }
    }

    public void Forget(string slug) => _states.TryRemove(slug, out _);

    private void Initialize(Account account)
    {
        using var scope = scopes.Create(account);
        var db = scope.ServiceProvider.GetRequiredService<LeagueDbContext>();
        db.Database.EnsureCreated();
        // WAL lets match pages read while the poller/backfill writes (the default
        // rollback journal blocks readers for the whole write). Persistent setting.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL");

        var connection = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            var columnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, column, definition) in Upgrades)
            {
                if (!columnsByTable.TryGetValue(table, out var columns))
                {
                    columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using var pragma = connection.CreateCommand();
                    pragma.CommandText = $"PRAGMA table_info({table})";
                    using var reader = pragma.ExecuteReader();
                    while (reader.Read()) columns.Add(reader.GetString(1));
                    columnsByTable[table] = columns;
                }
                if (columns.Contains(column)) continue;
                // Same raw command path as the PRAGMA above: the identifiers come
                // from the Upgrades table in this file, never from input, and DDL
                // cannot be parameterised anyway.
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
                alter.ExecuteNonQuery();
                columns.Add(column);
                log.LogInformation("Account {Slug}: added column {Table}.{Column}", account.Slug, table, column);
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }
        log.LogInformation("Account {Slug}: {RiotId} at {Dir}", account.Slug, account.RiotId, account.DataDir);
    }
}
