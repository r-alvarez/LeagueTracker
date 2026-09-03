using LeagueTracker.Api.Accounts;
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

    private AgentKeyStore Store(bool allowUnbound = false)
    {
        var registry = new RegistryDatabase(_server, Options.Create(new AccountsOptions { DataRoot = _root }), Options.Create(new RiotOptions()), new TestEnv(_root));
        registry.Migrate(NullLogger.Instance);
        return new AgentKeyStore(registry, Options.Create(new AgentsOptions { AllowUnbound = allowUnbound }), NullLogger<AgentKeyStore>.Instance);
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
        Assert.Equal(EnrolRefusal.JoinCodeRequired, keys.Enroll(Key(2), "other", "PC", "203.0.113.5", code.Code).Refusal);
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
    public void An_address_gets_twenty_first_announcements_an_hour()
    {
        var keys = Store();
        for (var i = 0; i < 20; i++) Assert.Equal(EnrolRefusal.JoinCodeRequired, keys.Enroll(Key(i), "guess", "PC", "203.0.113.5", "WRONGCODE").Refusal);
        Assert.Equal(EnrolRefusal.TooManyAttempts, keys.Enroll(Key(21), "guess", "PC", "203.0.113.5", "WRONGCODE").Refusal);
        var code = keys.MintJoinCode("user-1", AgentRole.Recorder);
        Assert.Null(keys.Enroll(Key(22), "friend", "PC", "198.51.100.7", code.Code).Refusal);
    }
}
