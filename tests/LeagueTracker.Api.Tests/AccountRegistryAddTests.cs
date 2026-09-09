using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Riot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

[Collection(PostgresCollection.Name)]
public class AccountRegistryAddTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));
    private readonly DatabaseServer _server = postgres.NewServer();

    private AccountRegistry Registry()
    {
        var options = Options.Create(new AccountsOptions { DataRoot = _root, List = [new Account { GameName = "Seed", TagLine = "EUW", DataDir = Path.Combine(_root, "seed") }] });
        var riot = Options.Create(new RiotOptions());
        var env = new TestEnv(_root);
        return new AccountRegistry(options, riot, new RegistryDatabase(_server, options, riot, env), env, NullLogger<AccountRegistry>.Instance);
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    // N17: the row was written after the in-memory registration, so a puuid
    // the database refused left a phantom slug resolvable until restart.
    [Fact]
    public void A_puuid_the_database_refuses_leaves_no_phantom_account()
    {
        var registry = Registry();
        registry.Add("First", "0001", "euw1", null, "puuid-shared");

        Assert.Throws<AccountConflictException>(() => registry.Add("Second", "0002", "euw1", null, "puuid-shared"));

        Assert.Null(registry.BySlug("Second-0002"));
        Assert.Equal(["Seed-EUW", "First-0001"], registry.All.Select(a => a.Slug));
        Assert.Equal(registry.All.Select(a => a.Slug), Registry().All.Select(a => a.Slug));
    }
}
