using System.Net;
using System.Text.Json;
using LeagueTracker.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Tests;

public class Auth0ManagementClientTests
{
    private const string Issuer = "https://tenant.eu.auth0.com/";

    private sealed class FakeAuth0 : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string? Authorization, string Body)> Requests = [];
        public readonly Dictionary<string, Func<(HttpStatusCode, string)>> Answers = new();
        public int TokenCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method, path, request.Headers.Authorization?.ToString(), body));
            if (path == "/oauth/token")
            {
                TokenCalls++;
                return Json(HttpStatusCode.OK, """{"access_token":"tok-1","expires_in":86400,"token_type":"Bearer"}""");
            }
            var (status, text) = Answers.TryGetValue($"{request.Method} {path}", out var answer) ? answer() : (HttpStatusCode.NotFound, """{"message":"no canned answer"}""");
            return Json(status, text);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string text) =>
            new(status) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (Auth0ManagementClient Client, FakeAuth0 Fake) Make()
    {
        var fake = new FakeAuth0();
        var options = Options.Create(new AuthOptions
        {
            Oidc = new OidcOptions { Authority = Issuer, ClientId = "login-client" },
            Management = new ManagementOptions { ClientId = "m2m", ClientSecret = "s3cret" },
        });
        return (new Auth0ManagementClient(new Factory(fake), options, NullLogger<Auth0ManagementClient>.Instance), fake);
    }

    [Fact]
    public async Task Token_is_fetched_once_and_reused_across_management_calls()
    {
        var (client, fake) = Make();
        fake.Answers["POST /api/v2/users"] = () => (HttpStatusCode.Created, """{"user_id":"auth0|abc"}""");
        fake.Answers["POST /api/v2/tickets/password-change"] = () => (HttpStatusCode.Created, """{"ticket":"https://tenant.eu.auth0.com/lo/reset?ticket=xyz"}""");

        var id = await client.EnsureUserAsync("friend@example.com", "Friend", CancellationToken.None);
        var (url, _) = await client.PasswordSetupLinkAsync(id, "https://league.example/", TimeSpan.FromDays(7), CancellationToken.None);

        Assert.Equal("auth0|abc", id);
        Assert.StartsWith("https://tenant.eu.auth0.com/lo/reset", url);
        Assert.Equal(1, fake.TokenCalls);
        Assert.All(fake.Requests.Where(r => r.Path.StartsWith("/api/v2/")), r => Assert.Equal("Bearer tok-1", r.Authorization));

        var token = fake.Requests.Single(r => r.Path == "/oauth/token");
        using var doc = JsonDocument.Parse(token.Body);
        Assert.Equal("client_credentials", doc.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("m2m", doc.RootElement.GetProperty("client_id").GetString());
        Assert.Equal($"{Issuer}api/v2/", doc.RootElement.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task Create_user_sends_the_connection_and_a_throwaway_password_and_no_verification_mail()
    {
        var (client, fake) = Make();
        fake.Answers["POST /api/v2/users"] = () => (HttpStatusCode.Created, """{"user_id":"auth0|abc"}""");

        await client.EnsureUserAsync("friend@example.com", null, CancellationToken.None);

        using var doc = JsonDocument.Parse(fake.Requests.Single(r => r.Path == "/api/v2/users").Body);
        var root = doc.RootElement;
        Assert.Equal("friend@example.com", root.GetProperty("email").GetString());
        Assert.Equal("Username-Password-Authentication", root.GetProperty("connection").GetString());
        Assert.False(root.GetProperty("email_verified").GetBoolean());
        Assert.False(root.GetProperty("verify_email").GetBoolean());
        Assert.True(root.GetProperty("password").GetString()!.Length >= 20);
    }

    [Fact]
    public async Task Existing_user_at_auth0_is_found_by_email_preferring_the_database_connection()
    {
        var (client, fake) = Make();
        fake.Answers["POST /api/v2/users"] = () => (HttpStatusCode.Conflict, """{"statusCode":409,"error":"Conflict","message":"The user already exists.","errorCode":"auth0_idp_error"}""");
        fake.Answers["GET /api/v2/users-by-email?email=friend%40example.com"] = () => (HttpStatusCode.OK, """
            [
              {"user_id":"google-oauth2|111","identities":[{"connection":"google-oauth2"}]},
              {"user_id":"auth0|222","identities":[{"connection":"Username-Password-Authentication"}]}
            ]
            """);

        var id = await client.EnsureUserAsync("friend@example.com", null, CancellationToken.None);

        Assert.Equal("auth0|222", id);
    }

    [Fact]
    public async Task Password_setup_mail_goes_through_the_authentication_api_with_the_login_client_id_and_no_token()
    {
        var (client, fake) = Make();
        fake.Answers["POST /dbconnections/change_password"] = () => (HttpStatusCode.OK, """{"ok":true}""");

        await client.SendPasswordSetupEmailAsync("friend@example.com", CancellationToken.None);

        var mail = fake.Requests.Single(r => r.Path == "/dbconnections/change_password");
        Assert.Null(mail.Authorization);
        Assert.Equal(0, fake.TokenCalls);
        using var doc = JsonDocument.Parse(mail.Body);
        Assert.Equal("login-client", doc.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("Username-Password-Authentication", doc.RootElement.GetProperty("connection").GetString());
    }

    [Fact]
    public async Task Provider_errors_surface_their_message_and_status()
    {
        var (client, fake) = Make();
        fake.Answers["POST /api/v2/users"] = () => (HttpStatusCode.Forbidden, """{"statusCode":403,"error":"Forbidden","message":"Insufficient scope, expected any of: create:users"}""");

        var ex = await Assert.ThrowsAsync<Auth0Exception>(() => client.EnsureUserAsync("friend@example.com", null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
        Assert.Contains("create user", ex.Message);
        Assert.Contains("Insufficient scope", ex.Message);
    }

    [Fact]
    public async Task Deleting_a_user_already_gone_is_not_an_error()
    {
        var (client, fake) = Make();
        fake.Answers["DELETE /api/v2/users/auth0%7Cabc"] = () => (HttpStatusCode.NotFound, """{"message":"not found"}""");

        await client.DeleteUserAsync("auth0|abc", CancellationToken.None);
    }

    [Fact]
    public void Not_configured_without_both_the_login_client_and_the_management_credentials()
    {
        var loginOnly = new Auth0ManagementClient(new Factory(new FakeAuth0()),
            Options.Create(new AuthOptions { Oidc = new OidcOptions { Authority = Issuer, ClientId = "login-client" } }), NullLogger<Auth0ManagementClient>.Instance);
        Assert.False(loginOnly.Configured);
        Assert.True(Make().Client.Configured);
    }
}
