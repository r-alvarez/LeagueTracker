using System.Net;
using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

// Ownership is the one thing a claim hands out, so what happens when two
// people (or two clicks) race for it matters more than the happy path
// (audit F02).
[Collection(PostgresCollection.Name)]
public class ClaimServiceTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));
    private readonly DatabaseServer _server = postgres.NewServer();
    private readonly RiotStub _riot = new();

    private (ClaimService Claims, AccountRegistry Accounts, RegistryDatabase Registry) Build(int maxPerUser = 5)
    {
        var config = Options.Create(new AccountsOptions { DataRoot = _root, MaxAccountsPerUser = maxPerUser, List = [new Account { GameName = "Player", TagLine = "TEST", DataDir = _root, Puuid = "puuid-1" }] });
        var riot = Options.Create(new RiotOptions());
        var env = new TestEnv(_root);
        var registry = new RegistryDatabase(_server, config, riot, env);
        var accounts = new AccountRegistry(config, riot, registry, env, NullLogger<AccountRegistry>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(accounts);
        services.AddScoped<AccountContext>();
        services.AddScoped(sp => new RiotApiClient(new HttpClient(_riot), sp.GetRequiredService<AccountContext>()));
        // The puuid is configured, so the player service never opens its database.
        services.AddScoped(sp => new TrackedPlayerService(null!, sp.GetRequiredService<RiotApiClient>(), sp.GetRequiredService<AccountContext>()));
        var provider = services.BuildServiceProvider();
        var scopes = new AccountScopes(provider.GetRequiredService<IServiceScopeFactory>());
        return (new ClaimService(registry, accounts, scopes, NullLogger<ClaimService>.Instance), accounts, registry);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    private static void SeedPendingClaim(RegistryDatabase registry, string userId, string accountId, int iconId)
    {
        using var db = registry.Open();
        db.OwnershipClaims.Add(new OwnershipClaim
        {
            Id = userId, UserId = userId, AccountId = accountId, IconId = iconId,
            State = ClaimState.Pending, CreatedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddMinutes(10),
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Two_concurrent_verifies_of_the_same_account_have_one_winner()
    {
        var (claims, accounts, registry) = Build();
        var account = accounts.Default;
        SeedPendingClaim(registry, "claimant-a", account.Id, 10);
        SeedPendingClaim(registry, "claimant-b", account.Id, 10);
        _riot.Icon = 10;
        _riot.HoldUntilRequests(1);

        var results = await Task.WhenAll(
            claims.VerifyAsync("claimant-a", "claimant-a", CancellationToken.None),
            claims.VerifyAsync("claimant-b", "claimant-b", CancellationToken.None));

        var winner = Assert.Single(results, r => r.Verified);
        var loser = Assert.Single(results, r => !r.Verified);
        Assert.Equal("verified", winner.Claim!.State);
        Assert.Equal("failed", loser.Claim!.State);
        Assert.Equal(winner.Claim.Id, account.OwnerUserId);
        using var db = registry.Open();
        Assert.Equal(winner.Claim.Id, db.Accounts.Single(a => a.Id == account.Id).OwnerUserId);
        Assert.Equal(1, _riot.Requests);
    }

    [Fact]
    public async Task A_stranger_cannot_start_while_another_persons_challenge_is_live()
    {
        var (claims, accounts, _) = Build();
        _riot.Icon = 3;

        var (first, _) = await claims.StartAsync("claimant-a", accounts.Default.Id, CancellationToken.None);
        var (second, error) = await claims.StartAsync("claimant-b", accounts.Default.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Contains("someone else", error);
        Assert.Equal(1, _riot.Requests);
    }

    [Fact]
    public async Task Restarting_your_own_claim_replaces_it()
    {
        var (claims, accounts, registry) = Build();
        _riot.Icon = 3;

        var (first, _) = await claims.StartAsync("claimant-a", accounts.Default.Id, CancellationToken.None);
        var (second, _) = await claims.StartAsync("claimant-a", accounts.Default.Id, CancellationToken.None);

        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Equal([second.Id], claims.Mine("claimant-a").Select(c => c.Id));
        using var db = registry.Open();
        Assert.Equal(ClaimState.Expired, db.OwnershipClaims.Single(c => c.Id == first.Id).State);
    }

    [Fact]
    public async Task A_cached_wrong_icon_does_not_cost_an_attempt()
    {
        var (claims, accounts, registry) = Build();
        SeedPendingClaim(registry, "claimant-a", accounts.Default.Id, 10);
        _riot.Icon = 4;

        var (real, _, _) = await claims.VerifyAsync("claimant-a", "claimant-a", CancellationToken.None);
        var (cached, _, error) = await claims.VerifyAsync("claimant-a", "claimant-a", CancellationToken.None);

        Assert.Equal(2, real!.AttemptsLeft);
        Assert.Equal(2, cached!.AttemptsLeft);
        Assert.Contains("when we last asked", error);
        Assert.Equal(1, _riot.Requests);
    }

    // summoner-v4 with one icon for every puuid; can hold its answers until
    // N callers are waiting, so the callers provably race past it together.
    private sealed class RiotStub : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdUntil;
        private int _requests;

        public int Icon { get; set; }
        public int Requests => _requests;

        public void HoldUntilRequests(int count) => _holdUntil = count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _requests) >= _holdUntil) _release.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"profileIconId\":{Icon}}}") };
        }
    }

    // Review of N5: adding checked the per-user ceiling, claiming did not.
    [Fact]
    public async Task A_user_at_the_ceiling_cannot_claim_another_account_but_may_claim_one_they_added()
    {
        var (claims, accounts, registry) = Build(maxPerUser: 1);
        var mine = accounts.Add("Mine", "0001", "euw1", null, "puuid-mine", "claimant-a");
        var target = accounts.Default;
        SeedPendingClaim(registry, "claimant-a", target.Id, 10);
        _riot.Icon = 10;

        var refused = await claims.VerifyAsync("claimant-a", "claimant-a", CancellationToken.None);
        Assert.False(refused.Verified);
        Assert.Contains("most accounts", refused.Error);
        Assert.Equal("pending", refused.Claim!.State);
        Assert.Null(accounts.ById(target.Id)!.OwnerUserId);

        Assert.False(accounts.CannotTakeOn("claimant-a", mine));
        Assert.Contains("most accounts", (await claims.StartAsync("claimant-a", target.Id, CancellationToken.None)).Error);
    }
}
