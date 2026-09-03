using System.Collections.Concurrent;
using LeagueTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Accounts;

// Creates/upgrades an account's schema and remembers whether that worked.
// One account's broken schema (the database unreachable at boot, a SQLite
// import that failed verification) must not take the other accounts down
// with it: it is marked unavailable, its own API answers 503 with the reason,
// the poller skips it, and the next request or poll pass after RetryEvery
// tries again - so a transient failure heals itself without a restart.
public sealed class AccountInitializer(DatabaseServer server, AccountScopes scopes, ILogger<AccountInitializer> log)
{
    public const string LegacyFileName = "leaguetracker.db";

    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(60);

    private sealed class State
    {
        public bool Ready;
        public string? Error;
        public DateTime LastAttemptUtc;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public bool IsReady(Account account) => _states.TryGetValue(account.Id, out var s) && s.Ready;

    // The reason the account is unavailable; null when it is fine (or untried).
    public string? ErrorFor(Account account) => _states.TryGetValue(account.Id, out var s) && !s.Ready ? s.Error : null;

    // True when the account can serve. A failed account is retried at most
    // every RetryEvery, whoever asks (a request, the poller, the boot loop).
    public bool EnsureReady(Account account)
    {
        var state = _states.GetOrAdd(account.Id, _ => new State());
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

    public void Forget(string accountId) => _states.TryRemove(accountId, out _);

    public static string LegacyFile(Account account) => Path.Combine(account.DataDir, LegacyFileName);

    private void Initialize(Account account)
    {
        var schema = DatabaseServer.AccountSchema(account);
        server.EnsureSchema(schema);
        using var scope = scopes.Create(account);
        var db = scope.ServiceProvider.GetRequiredService<LeagueDbContext>();
        db.Database.Migrate();
        SqliteImport.ImportIfPending(db, LegacyFile(account), $"Account {account.Slug}", log);
        log.LogInformation("Account {Slug}: {RiotId} in schema {Schema}, files at {Dir}", account.Slug, account.RiotId, schema, account.DataDir);
    }
}
