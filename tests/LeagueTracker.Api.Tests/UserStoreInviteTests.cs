using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Auth;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Riot;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

public class UserStoreInviteTests : IDisposable
{
    private const string Issuer = "https://tenant.eu.auth0.com/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lt-tests", Guid.NewGuid().ToString("N"));

    private UserStore Store(bool inviteOnly = true)
    {
        var registry = new RegistryDatabase(Options.Create(new AccountsOptions { DataRoot = _root }), Options.Create(new RiotOptions()), new Env(_root));
        registry.EnsureCreated(NullLogger.Instance);
        return new UserStore(registry, Options.Create(new AuthOptions { InviteOnly = inviteOnly }), NullLogger<UserStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a locked WAL file on Windows; the temp folder is disposable */ }
    }

    [Fact]
    public void Invite_makes_a_pending_row_once_per_address()
    {
        var users = Store();
        var admin = users.EnsureByEmail("admin@example.com", admin: true);

        var invited = users.Invite("Friend@Example.com ", "Friend", admin.Id);

        Assert.NotNull(invited);
        Assert.Equal("friend@example.com", invited.Email);
        Assert.Equal("Friend", invited.DisplayName);
        Assert.True(invited.IsInvitedPending);
        Assert.Equal(admin.Id, invited.InvitedByUserId);
        Assert.Null(users.Invite("friend@example.com", null, admin.Id));
    }

    [Fact]
    public void Invite_only_refuses_a_stranger_even_with_a_verified_email()
    {
        var users = Store();

        var ex = Assert.Throws<NotInvitedException>(() => users.FromLogin(Issuer, "auth0|stranger", "stranger@example.com", emailVerified: true, "Stranger"));

        Assert.Equal("stranger@example.com", ex.Email);
        Assert.Null(users.ByEmail("stranger@example.com"));
    }

    [Fact]
    public void Without_invite_only_a_first_login_still_creates_the_user()
    {
        var users = Store(inviteOnly: false);

        var (user, created) = users.FromLogin(Issuer, "auth0|stranger", "stranger@example.com", emailVerified: true, "Stranger");

        Assert.True(created);
        Assert.Equal("stranger@example.com", user.Email);
    }

    [Fact]
    public void Invited_person_signs_in_on_the_identity_we_created_whatever_the_provider_says_about_the_email()
    {
        var users = Store();
        var admin = users.EnsureByEmail("admin@example.com", admin: true);
        var invited = users.Invite("friend@example.com", "Friend", admin.Id)!;
        users.SetProviderUserId(invited.Id, "auth0|abc");

        var (user, created) = users.FromLogin(Issuer, "auth0|abc", "friend@example.com", emailVerified: false, "Friend");

        Assert.False(created);
        Assert.Equal(invited.Id, user.Id);
        Assert.NotNull(user.LastSeenUtc);
        Assert.False(users.ById(user.Id)!.IsInvitedPending);
        Assert.Contains(users.ById(user.Id)!.Logins, l => l.Issuer == Issuer && l.Subject == "auth0|abc");
    }

    [Fact]
    public void Invited_person_who_signs_in_with_a_verified_social_login_joins_by_email()
    {
        var users = Store();
        var admin = users.EnsureByEmail("admin@example.com", admin: true);
        var invited = users.Invite("friend@example.com", null, admin.Id)!;

        var (user, created) = users.FromLogin(Issuer, "google-oauth2|111", "friend@example.com", emailVerified: true, "Friend G");

        Assert.False(created);
        Assert.Equal(invited.Id, user.Id);
    }

    [Fact]
    public void Only_a_pending_invite_can_be_removed()
    {
        var users = Store();
        var admin = users.EnsureByEmail("admin@example.com", admin: true);
        var invited = users.Invite("friend@example.com", null, admin.Id)!;
        users.SetProviderUserId(invited.Id, "auth0|abc");

        Assert.False(users.DeleteInvited(admin.Id));
        users.FromLogin(Issuer, "auth0|abc", "friend@example.com", emailVerified: false, null);
        Assert.False(users.DeleteInvited(invited.Id));

        var other = users.Invite("other@example.com", null, admin.Id)!;
        Assert.True(users.DeleteInvited(other.Id));
        Assert.Null(users.ByEmail("other@example.com"));
    }

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
