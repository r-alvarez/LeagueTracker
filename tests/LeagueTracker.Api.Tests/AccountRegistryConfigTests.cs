using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Riot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

// The public compose asserts a friend's account from stack env vars that may
// be unset; that must read as "nothing to assert", not as a broken boot.
[Collection(PostgresCollection.Name)]
public class AccountRegistryConfigTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));
    private readonly DatabaseServer _server = postgres.NewServer();

    private AccountRegistry Registry(params Account[] configured)
    {
        var options = Options.Create(new AccountsOptions { DataRoot = _root, List = [.. configured] });
        var riot = Options.Create(new RiotOptions());
        var env = new TestEnv(_root);
        return new AccountRegistry(options, riot, new RegistryDatabase(_server, options, riot, env), env, NullLogger<AccountRegistry>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    [Fact]
    public void An_entry_with_no_riot_id_at_all_is_skipped()
    {
        var registry = Registry(
            new Account { GameName = "ImRA", TagLine = "87166", DataDir = Path.Combine(_root, "main") },
            new Account { DataDir = Path.Combine(_root, "ben") });
        Assert.Equal(["ImRA-87166"], registry.All.Select(a => a.Slug));
    }

    [Fact]
    public void Half_a_riot_id_still_refuses_to_boot()
    {
        Assert.Throws<InvalidOperationException>(() => Registry(new Account { GameName = "ImRA", DataDir = Path.Combine(_root, "main") }));
    }
}
