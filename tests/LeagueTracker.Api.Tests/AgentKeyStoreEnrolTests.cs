using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Auth;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

// Enrolment is the one anonymous write on the internet-facing api path, so
// what it refuses matters more than what it accepts (audit M-H6).
[Collection(PostgresCollection.Name)]
public class AgentKeyStoreEnrolTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));
    private readonly DatabaseServer _server = postgres.NewServer();
    private RegistryDatabase _registry = null!;

    private AgentKeyStore Store(bool allowUnbound = false)
    {
        var registry = new RegistryDatabase(_server, Options.Create(new AccountsOptions { DataRoot = _root }), Options.Create(new RiotOptions()), new TestEnv(_root));
        registry.Migrate(NullLogger.Instance);
        _registry = registry;
        return new AgentKeyStore(registry, Options.Create(new AgentsOptions { AllowUnbound = allowUnbound }), NullLogger<AgentKeyStore>.Instance);
    }

    [Fact]
    public void Discovery_includes_shared_pc_grants_and_observes_their_removal()
    {
        const AgentRole role = AgentRole.Recorder;
        var keys = Store();
        var code = keys.MintJoinCode("owner", role);
        var agent = keys.Enroll(Key(1), "shared-pc", "PC", "203.0.113.5", code.Code).Record!;
        keys.Decide(agent.Id, AgentKeyStatus.Approved);
        keys.Assign(agent.Id, "owner", role, ["shared"]);
        var caller = CallerFor(keys, agent);
        Account[] accounts = [new() { Id = "mine", OwnerUserId = "owner" }, new() { Id = "shared", OwnerUserId = "friend" }, new() { Id = "unrelated", OwnerUserId = "stranger" }];

        Assert.Equal(new[] { "mine", "shared" }, caller.DiscoverAgentAccounts(accounts).Select(a => a.Id));
        keys.Assign(agent.Id, "owner", role, []);
        Assert.Equal(new[] { "mine" }, caller.DiscoverAgentAccounts(accounts).Select(a => a.Id));
    }

    [Theory]
    [InlineData(AgentRole.Renderer, false, 2)]
    [InlineData(AgentRole.Recorder, true, 2)]
    [InlineData(AgentRole.Recorder, false, 0)]
    public void Discovery_preserves_renderer_and_unbound_rollout_scope(AgentRole role, bool allowUnbound, int expected)
    {
        var keys = Store(allowUnbound);
        var code = keys.MintJoinCode("owner", role);
        var agent = keys.Enroll(Key(1), "pc", "PC", "203.0.113.5", code.Code).Record!;
        keys.Decide(agent.Id, AgentKeyStatus.Approved);
        keys.Assign(agent.Id, null, role);
        Account[] accounts = [new() { Id = "one", OwnerUserId = "owner" }, new() { Id = "two", OwnerUserId = "friend" }];
        Assert.Equal(expected, CallerFor(keys, agent).DiscoverAgentAccounts(accounts).Count());
    }

    private Caller CallerFor(AgentKeyStore keys, AgentKeyRecord agent)
    {
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(TrackerClaims.AgentId, agent.Id)], AgentKeyAuthenticationHandler.SchemeName)) },
        };
        return new Caller(http, keys, new UserStore(_registry, Options.Create(new AuthOptions()), NullLogger<UserStore>.Instance));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a disposable temp folder */ }
    }

    private static string Key(int n) => $"key-{n:D3}-" + new string('x', 40);

    [Fact]
    public void Without_a_join_code_a_new_machine_is_refused_not_parked()
    {
        var keys = Store();
        var (record, _, refusal) = keys.Enroll(Key(1), "render-box", "PC", "203.0.113.5", joinCode: null);
        Assert.Null(record);
        Assert.Equal(EnrolRefusal.JoinCodeRequired, refusal);
        Assert.Empty(keys.All);
    }

    [Fact]
    public void A_join_code_makes_a_pending_record_bound_to_its_owner()
    {
        var keys = Store();
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
        var (record, created, refusal) = keys.Enroll(Key(1), "gaming-pc", "PC", "203.0.113.5", code.Code);
        Assert.Null(refusal);
        Assert.True(created);
        Assert.Equal("user-1", record!.OwnerUserId);
        Assert.Equal(AgentKeyStatus.Pending, record.Status);
        Assert.Equal(EnrolRefusal.JoinCodeUnusable, keys.Enroll(Key(2), "other", "PC", "203.0.113.5", code.Code).Refusal);
    }

    [Fact]
    public void The_same_key_re_announcing_is_never_refused()
    {
        var keys = Store();
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
        var first = keys.Enroll(Key(1), "gaming-pc", "PC", "203.0.113.5", code.Code).Record!;
        for (var i = 0; i < 30; i++)
        {
            var (again, created, refusal) = keys.Enroll(Key(1), "gaming-pc", "PC", "203.0.113.5", joinCode: null);
            Assert.Null(refusal);
            Assert.False(created);
            Assert.Equal(first.Id, again!.Id);
        }
    }

    [Fact]
    public void Codeless_enrolments_are_capped_per_address_only_while_the_rollout_flag_admits_them()
    {
        var keys = Store(allowUnbound: true);
        Assert.Null(keys.Enroll(Key(1), "a", "PC", "203.0.113.5", null).Refusal);
        Assert.Null(keys.Enroll(Key(2), "b", "PC", "203.0.113.5", null).Refusal);
        Assert.Null(keys.Enroll(Key(3), "c", "PC", "203.0.113.5", null).Refusal);
        Assert.Equal(EnrolRefusal.TooManyPending, keys.Enroll(Key(4), "d", "PC", "203.0.113.5", null).Refusal);
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
        Assert.Null(keys.Enroll(Key(5), "friend", "PC", "203.0.113.5", code.Code).Refusal);
    }

    [Fact]
    public void Bound_pending_records_do_not_count_against_the_codeless_caps()
    {
        var keys = Store(allowUnbound: true);
        for (var i = 0; i < 20; i++)
        {
            var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
            Assert.Null(keys.Enroll(Key(i), $"m{i}", "PC", $"203.0.113.{i}", code.Code).Refusal);
        }
        Assert.Null(keys.Enroll(Key(99), "codeless", "PC", "198.51.100.7", null).Refusal);
    }

    [Fact]
    public void An_unbound_pending_record_nobody_has_seen_for_a_day_is_dropped()
    {
        var keys = Store(allowUnbound: true);
        var junk = keys.Enroll(Key(1), "junk", "PC", "203.0.113.5", null).Record!;
        keys.Enroll(Key(2), "junk2", "PC", "203.0.113.5", null);
        keys.Enroll(Key(3), "junk3", "PC", "203.0.113.5", null);
        Assert.Equal(EnrolRefusal.TooManyPending, keys.Enroll(Key(4), "real", "PC", "203.0.113.5", null).Refusal);

        keys.ById(junk.Id)!.LastSeenUtc = DateTime.UtcNow.AddHours(-25);

        Assert.Null(keys.Enroll(Key(4), "real", "PC", "203.0.113.5", null).Refusal);
        Assert.Null(keys.ById(junk.Id));
    }

    [Fact]
    public void An_address_gets_twenty_distinct_guesses_an_hour()
    {
        var keys = Store();
        for (var i = 0; i < 20; i++) Assert.Equal(EnrolRefusal.JoinCodeUnusable, keys.Enroll(Key(i), "guess", "PC", "203.0.113.5", $"WRONGCOD{i:D2}").Refusal);
        Assert.Equal(EnrolRefusal.TooManyAttempts, keys.Enroll(Key(21), "guess", "PC", "203.0.113.5", "WRONGCODE").Refusal);
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
        Assert.Null(keys.Enroll(Key(22), "friend", "PC", "198.51.100.7", code.Code).Refusal);
    }

    // Ben, 3 Sept: a code that had expired, one Test press after another,
    // until his own address was refused and the window blamed the server.
    [Fact]
    public void Re_presenting_one_dead_code_never_exhausts_the_budget()
    {
        var keys = Store();
        for (var i = 0; i < 50; i++)
            Assert.Equal(EnrolRefusal.JoinCodeUnusable, keys.Enroll(Key(1), "friend", "PC", "203.0.113.5", "K7Q2-9DFM").Refusal);
        // Case and dashes are the same code, not three more guesses.
        Assert.Equal(EnrolRefusal.JoinCodeUnusable, keys.Enroll(Key(1), "friend", "PC", "203.0.113.5", "k7q29dfm").Refusal);
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
        Assert.Null(keys.Enroll(Key(1), "friend", "PC", "203.0.113.5", code.Code).Refusal);
    }

    // The budget guards the stranger's door; an owner's code is not a knock.
    [Fact]
    public void A_valid_code_is_never_charged_against_the_guessing_budget()
    {
        var keys = Store();
        for (var i = 0; i < 19; i++) Assert.Equal(EnrolRefusal.JoinCodeUnusable, keys.Enroll(Key(i), "guess", "PC", "203.0.113.5", $"WRONGCOD{i:D2}").Refusal);
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
        Assert.Null(keys.Enroll(Key(50), "friend", "PC", "203.0.113.5", code.Code).Refusal);
        Assert.Equal(EnrolRefusal.JoinCodeUnusable, keys.Enroll(Key(51), "guess", "PC", "203.0.113.5", "WRONGCOD19").Refusal);
        Assert.Equal(EnrolRefusal.TooManyAttempts, keys.Enroll(Key(52), "guess", "PC", "203.0.113.5", "WRONGCOD20").Refusal);
    }

    // Audit N9: the budget is the first thing an unknown key meets, before
    // the registry is asked anything - so a spent address cannot even present
    // a good code, and the code stays open for the same person elsewhere.
    [Fact]
    public void A_spent_address_is_refused_before_its_code_is_looked_up()
    {
        var keys = Store();
        for (var i = 0; i < 20; i++) keys.Enroll(Key(i), "guess", "PC", "203.0.113.5", $"WRONGCOD{i:D2}");
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);

        Assert.Equal(EnrolRefusal.TooManyAttempts, keys.Enroll(Key(50), "friend", "PC", "203.0.113.5", code.Code).Refusal);

        Assert.Empty(keys.All);
        Assert.Contains(keys.OpenJoinCodes("user-1"), c => c.Code == code.Code);
        Assert.Null(keys.Enroll(Key(50), "friend", "PC", "198.51.100.7", code.Code).Refusal);
    }

    [Fact]
    public void A_code_presented_by_two_machines_binds_only_one()
    {
        var keys = Store();
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
        Assert.Null(keys.Enroll(Key(1), "first", "PC", "203.0.113.5", code.Code).Refusal);
        Assert.Equal(EnrolRefusal.JoinCodeUnusable, keys.Enroll(Key(2), "second", "PC", "203.0.113.6", code.Code).Refusal);
        Assert.Single(keys.All);
    }

    [Fact]
    public void A_machine_with_no_code_at_all_is_told_so_and_not_that_its_code_failed()
    {
        var keys = Store();
        Assert.Equal(EnrolRefusal.JoinCodeRequired, keys.Enroll(Key(1), "m", "PC", "203.0.113.5", joinCode: null).Refusal);
        Assert.Equal(EnrolRefusal.JoinCodeRequired, keys.Enroll(Key(2), "m", "PC", "203.0.113.5", joinCode: "   ").Refusal);
    }
}
