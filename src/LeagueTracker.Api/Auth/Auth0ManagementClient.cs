using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Auth;

// The tracker's hand at the provider: create the identity an invited person
// will sign in with, send them Auth0's own "set your password" mail, mint a
// link to hand over by other means, delete what we created. Management API
// calls ride a client-credentials token cached until shortly before it
// expires; the mail is the Authentication API, which needs no token.
public sealed class Auth0ManagementClient(IHttpClientFactory httpFactory, IOptions<AuthOptions> options, ILogger<Auth0ManagementClient> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(2);
    public const string HttpClientName = "auth0";

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiresUtc;

    public bool Configured => options.Value.Oidc.Configured && options.Value.Management.Configured;

    private string Issuer => options.Value.Oidc.Authority.TrimEnd('/') + "/";
    private string Api => $"{Issuer}api/v2/";
    private string Connection => options.Value.Management.Connection;
    private HttpClient Http => httpFactory.CreateClient(HttpClientName);

    // The provider's user id for this email - created now, or the one
    // already there (an invite re-sent after a half-finished first attempt).
    public async Task<string> EnsureUserAsync(string email, string? displayName, CancellationToken ct)
    {
        var body = new
        {
            email,
            name = displayName is { Length: > 0 } ? displayName : email,
            connection = Connection,
            password = ThrowawayPassword(),
            email_verified = false,
            verify_email = false,
        };
        using var resp = await SendAsync(HttpMethod.Post, "users", body, ct);
        if (resp.StatusCode is HttpStatusCode.Conflict)
        {
            return await FindUserIdAsync(email, ct) ?? throw new Auth0Exception($"Auth0 says a user with {email} exists but does not return it", HttpStatusCode.Conflict);
        }
        await EnsureSuccessAsync(resp, "create user", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("user_id").GetString() ?? throw new Auth0Exception("Auth0 created the user without a user_id", resp.StatusCode);
    }

    public async Task<string?> FindUserIdAsync(string email, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"users-by-email?email={Uri.EscapeDataString(email)}", null, ct);
        await EnsureSuccessAsync(resp, "look up user", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        string? fallback = null;
        foreach (var user in doc.RootElement.EnumerateArray())
        {
            var id = user.GetProperty("user_id").GetString();
            fallback ??= id;
            // Prefer the identity in our database connection - a social login
            // with the same address is a different user at Auth0.
            if (user.TryGetProperty("identities", out var identities)
                && identities.EnumerateArray().Any(i => i.TryGetProperty("connection", out var c) && c.GetString() == Connection))
                return id;
        }
        return fallback;
    }

    public async Task DeleteUserAsync(string userId, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Delete, $"users/{Uri.EscapeDataString(userId)}", null, ct);
        if (resp.StatusCode is HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(resp, "delete user", ct);
    }

    // Auth0 mails its "Change Password" template to the address, from the
    // tenant's email provider; the link in it sets the first password.
    public async Task SendPasswordSetupEmailAsync(string email, CancellationToken ct)
    {
        var body = new { client_id = options.Value.Oidc.ClientId, email, connection = Connection };
        using var content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync($"{Issuer}dbconnections/change_password", content, ct);
        await EnsureSuccessAsync(resp, "send password email", ct);
    }

    // The same link the mail carries, for handing over by other means.
    public async Task<(string Url, DateTime ExpiresUtc)> PasswordSetupLinkAsync(string userId, string resultUrl, TimeSpan ttl, CancellationToken ct)
    {
        var body = new { user_id = userId, result_url = resultUrl, ttl_sec = (int)ttl.TotalSeconds, mark_email_as_verified = true, includeEmailInRedirect = false };
        using var resp = await SendAsync(HttpMethod.Post, "tickets/password-change", body, ct);
        await EnsureSuccessAsync(resp, "create password link", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var url = doc.RootElement.GetProperty("ticket").GetString() ?? throw new Auth0Exception("Auth0 returned no ticket", resp.StatusCode);
        return (url, DateTime.UtcNow + ttl);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
        return await Http.SendAsync(request, ct);
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is { } cached && DateTime.UtcNow < _tokenExpiresUtc - TokenMargin) return cached;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_token is { } raced && DateTime.UtcNow < _tokenExpiresUtc - TokenMargin) return raced;
            var m = options.Value.Management;
            var body = new { grant_type = "client_credentials", client_id = m.ClientId, client_secret = m.ClientSecret, audience = Api };
            using var content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{Issuer}oauth/token", content, ct);
            await EnsureSuccessAsync(resp, "get management token", ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600);
            log.LogInformation("Auth0 management token obtained, valid until {Until:u}", _tokenExpiresUtc);
            return _token!;
        }
        finally { _tokenGate.Release(); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync(ct);
        string detail;
        try
        {
            using var doc = JsonDocument.Parse(text);
            detail = doc.RootElement.TryGetProperty("message", out var msg) ? msg.GetString() ?? text
                : doc.RootElement.TryGetProperty("error_description", out var desc) ? desc.GetString() ?? text
                : text;
        }
        catch (JsonException) { detail = text; }
        if (detail.Length > 300) detail = detail[..300];
        throw new Auth0Exception($"Auth0 could not {what}: HTTP {(int)resp.StatusCode} {detail}", resp.StatusCode);
    }

    // The person never learns this; the setup mail replaces it. Base64 of 24
    // random bytes plus one of each class Auth0's default policy may want.
    private static string ThrowawayPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "aZ9!";
}

public sealed class Auth0Exception(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
