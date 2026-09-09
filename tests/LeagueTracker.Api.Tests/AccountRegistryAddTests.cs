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

    private AccountRegistry Registry(int maxAccounts = 200, int maxPerUser = 5)
    {
        var options = Options.Create(new AccountsOptions
        {
            DataRoot = _root,
            List = [new Account { GameName = "Seed", TagLine = "EUW", DataDir = Path.Combine(_root, "seed") }],
            MaxAccounts = maxAccounts,
            MaxAccountsPerUser = maxPerUser,
        });
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
        registry.Add("First", "0001", "euw1", null, "puuid-shared", "user-a");

        var refused = Assert.Throws<AccountConflictException>(() => registry.Add("Second", "0002", "euw1", null, "puuid-shared", "user-a"));
        Assert.Contains("another name", refused.Message);

        Assert.Null(registry.BySlug("Second-0002"));
        Assert.Equal(["Seed-EUW", "First-0001"], registry.All.Select(a => a.Slug));
        Assert.Equal(registry.All.Select(a => a.Slug), Registry().All.Select(a => a.Slug));
    }

    // N5: every account is a permanent slot; the site refuses past the
    // ceilings instead of growing into the connection and backup walls.
    [Fact]
    public void The_global_ceiling_counts_config_accounts_too()
    {
        var registry = Registry(maxAccounts: 2);
        registry.Add("First", "0001", "euw1", null, "p1", "user-a");

        var refused = Assert.Throws<AccountQuotaException>(() => registry.Add("Second", "0002", "euw1", null, "p2", "user-b"));
        Assert.Contains("full", refused.Message);
        Assert.True(registry.AtCapacity);
    }

    [Fact]
    public void A_user_is_charged_for_what_they_own_and_what_they_added_unclaimed()
    {
        var registry = Registry(maxPerUser: 2);
        var first = registry.Add("First", "0001", "euw1", null, "p1", "user-a");
        registry.Add("Second", "0002", "euw1", null, "p2", "user-a");
        Assert.True(registry.AtCapacityFor("user-a"));
        Assert.Throws<AccountQuotaException>(() => registry.Add("Third", "0003", "euw1", null, "p3", "user-a"));

        registry.Update(first, a => a.OwnerUserId = "user-b");
        Assert.False(registry.AtCapacityFor("user-a"));
        registry.Add("Third", "0003", "euw1", null, "p3", "user-a");
    }

    [Fact]
    public void The_adder_may_untrack_only_while_nobody_has_claimed_it()
    {
        var registry = Registry();
        var added = registry.Add("First", "0001", "euw1", null, "p1", "user-a");
        Assert.True(registry.MayUntrack(added, "user-a", admin: false));
        Assert.False(registry.MayUntrack(added, "user-b", admin: false));

        registry.Update(added, a => a.OwnerUserId = "user-b");
        Assert.False(registry.MayUntrack(added, "user-a", admin: false));
        Assert.True(registry.MayUntrack(added, "user-b", admin: false));
        Assert.True(registry.MayUntrack(added, null, admin: true));
    }

    // Review of N5, second pass: adds and claims judged the ceiling under two
    // different locks, so one of each in flight could both pass.
    [Fact]
    public async Task A_concurrent_add_and_claim_cannot_both_pass_the_ceiling()
    {
        var registry = Registry(maxPerUser: 1);
        var unowned = Enumerable.Range(0, 8).Select(i => registry.Add($"Open{i}", "0000", "euw1", null, $"open-{i}", $"adder-{i}")).ToList();

        var attempts = Enumerable.Range(0, 8).SelectMany(i => new Func<Task>[]
        {
            () => Task.Run(() =>
            {
                try { registry.Add($"Mine{i}", "0000", "euw1", null, $"mine-{i}", "user-a"); }
                catch (AccountQuotaException) { /* the ceiling did its job */ }
            }),
            () => Task.Run(() => registry.TakeOwnership(unowned[i], "user-a", () => true)),
        });
        await Task.WhenAll(attempts.Select(start => start()));

        Assert.Equal(1, registry.CountedAgainst("user-a"));
    }
}
