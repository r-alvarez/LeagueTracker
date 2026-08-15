using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LeagueTracker.RenderAgent;

public enum UploadOutcome
{
    Uploaded,   // done - Url carries the watch link
    Paused,     // a game wants the machine (or a deploy stop) - resume at the next idle sweep
    Postponed,  // quota, network, expired session - retrying later can succeed
    Failed,     // deterministic (rejected request, revoked auth) - retrying cannot
}

public sealed record UploadResult(UploadOutcome Outcome, string? Url = null, string? Error = null);

/// Publishes finished recordings to the player's YouTube channel - the
/// storage-free review mode (UploadVodSidecars) with the manual
/// upload-and-paste step automated away.
///
/// YouTube has no service accounts: uploads act as the channel's Google
/// account via OAuth. "--youtube-auth" runs the one-time browser consent and
/// stores the refresh token in youtube-token.json next to the exe; from then
/// on the uploader mints its own short-lived access tokens. Uploads use the
/// resumable protocol with the session URI persisted per game, so an upload
/// interrupted by a new game, a deploy or a dead connection continues from
/// the last acknowledged byte instead of re-sending gigabytes.
public sealed class YouTubeUploader(AgentConfig config)
{
    /// upload alone suffices for videos.insert; readonly is only so the auth
    /// flow's channels.list can NAME the channel it just bound (upload-only
    /// tokens get 403 insufficientPermissions on any read, learned live).
    private const string Scope = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UploadEndpoint =
        "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";

    /// 16MB pieces (the protocol wants multiples of 256KB): small enough that
    /// the between-chunks pause check reacts to a game launching within tens
    /// of seconds even on modest upstream, large enough not to throttle.
    private const int ChunkBytes = 16 * 1024 * 1024;

    private static string TokenPath => Path.Combine(AppContext.BaseDirectory, "youtube-token.json");

    /// Google's resume responses reuse 308 with no Location header; auto-
    /// redirect off keeps HttpClient from ever second-guessing that.
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    private string? _accessToken;
    private DateTime _accessTokenExpiresUtc;
    // The credentials that were found broken (blank client, missing or revoked
    // refresh token). Tied to the values, not a flag: a new token arriving from
    // the tracker's profile un-breaks the uploader without a restart.
    private string? _brokenCredentials;
    private bool _authBroken
    {
        get => _brokenCredentials is not null && _brokenCredentials == Credentials;
        set => _brokenCredentials = value ? Credentials : null;
    }
    private string Credentials => $"{config.YouTubeClientId}|{config.YouTubeClientSecret}|{LoadRefreshToken()}";
    private DateTime _backoffUntilUtc; // quota spent - retrying sooner cannot succeed

    public bool Enabled => config.YouTubeUpload && !_authBroken;

    /// Loud startup verdict: a misconfigured uploader must say so once and
    /// clearly, not fail quietly after every game.
    public void ValidateAtStartup()
    {
        if (!config.YouTubeUpload) return;
        if (config.YouTubeClientId is not { Length: > 0 } || config.YouTubeClientSecret is not { Length: > 0 })
        {
            _authBroken = true;
            Log.Error("YouTubeUpload is on but YouTubeClientId/YouTubeClientSecret are blank - uploads disabled " +
                      "(Google Cloud Console > Credentials > OAuth client ID, type \"Desktop app\")");
            return;
        }
        if (LoadRefreshToken() is null)
        {
            _authBroken = true;
            Log.Error($"YouTubeUpload is on but there is no refresh token - the tracker's agent profile supplies one (YouTubeRefreshToken), or run --youtube-auth here once to authorize the channel");
            return;
        }
        Log.Info($"YouTube uploads on ({Visibility}) - finished recordings publish to the authorized channel");
    }

    private string Visibility => config.YouTubeVisibility.Trim().ToLowerInvariant() switch
    {
        ("public" or "private") and var v => v,
        _ => "unlisted",
    };

    /// One recording -> one YouTube video. sessionPath persists the resumable
    /// session across pauses and restarts. holdOff is polled between chunks -
    /// true means a game needs the bandwidth (and the loop this runs on) more
    /// than the upload does.
    public async Task<UploadResult> UploadAsync(string mp4Path, string title, string description,
        string sessionPath, Func<bool> holdOff, CancellationToken ct)
    {
        if (_authBroken) return new(UploadOutcome.Failed, Error: "authorization broken - run --youtube-auth");
        if (DateTime.UtcNow < _backoffUntilUtc)
        {
            return new(UploadOutcome.Postponed, Error: "YouTube quota backoff still active");
        }
        try
        {
            return await UploadCoreAsync(mp4Path, title, description, sessionPath, holdOff, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            // Dead wifi, dropped socket, chunk timeout: the persisted session
            // resumes from the last acknowledged byte next sweep.
            return new(UploadOutcome.Postponed, Error: ex.Message);
        }
    }

    private async Task<UploadResult> UploadCoreAsync(string mp4Path, string title, string description,
        string sessionPath, Func<bool> holdOff, CancellationToken ct)
    {
        var size = new FileInfo(mp4Path).Length;

        // An earlier attempt's session continues where it stopped; Google
        // keeps them alive for days, and 404/410 just means start over.
        var (sessionUri, offset) = await ResumeSessionAsync(sessionPath, size, ct);
        if (sessionUri is null)
        {
            var started = await StartSessionAsync(size, title, description, ct);
            if (started.Error is not null) return started.Error;
            sessionUri = started.Uri!;
            offset = 0;
            File.WriteAllText(sessionPath, JsonSerializer.Serialize(new { uri = sessionUri, size }));
        }

        await using var file = File.OpenRead(mp4Path);
        file.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[ChunkBytes];
        while (true)
        {
            if (RenderAgent.StopRequested) return new(UploadOutcome.Paused, Error: "agent stop requested");
            if (holdOff()) return new(UploadOutcome.Paused, Error: "a game is starting");
            ct.ThrowIfCancellationRequested();

            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkBytes, size - offset)), ct);
            if (read == 0) return new(UploadOutcome.Failed, Error: $"file ended at {offset} of {size} bytes");
            using var req = new HttpRequestMessage(HttpMethod.Put, sessionUri)
            {
                Content = new ByteArrayContent(buffer, 0, read),
            };
            req.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, size);
            using var resp = await _http.SendAsync(req, ct);

            if ((int)resp.StatusCode == 308) // "Resume Incomplete" - more bytes wanted
            {
                offset = NextOffset(resp) ?? offset + read;
                file.Seek(offset, SeekOrigin.Begin);
                continue;
            }
            if (resp.IsSuccessStatusCode)
            {
                var id = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct))
                    .RootElement.GetProperty("id").GetString();
                TryDelete(sessionPath);
                return new(UploadOutcome.Uploaded, Url: $"https://youtu.be/{id}");
            }
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                TryDelete(sessionPath);
                return new(UploadOutcome.Postponed, Error: "upload session expired - starting over next sweep");
            }
            return Classify(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
    }

    /// Asks the stored session where it stands ("bytes */size" probe).
    /// (null, 0) = no usable session; otherwise the URI and the next byte
    /// YouTube wants.
    private async Task<(string? Uri, long Offset)> ResumeSessionAsync(string sessionPath, long size, CancellationToken ct)
    {
        string? uri = null;
        try
        {
            var saved = JsonDocument.Parse(File.ReadAllText(sessionPath)).RootElement;
            // A re-finalized mp4 (game resumed and grew) is a different
            // upload - the old session's bytes are for a file that no longer
            // exists.
            if (saved.GetProperty("size").GetInt64() == size) uri = saved.GetProperty("uri").GetString();
        }
        catch
        {
            return (null, 0); // no/corrupt session file - start fresh
        }
        if (uri is null)
        {
            TryDelete(sessionPath);
            return (null, 0);
        }
        using var req = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent([]) };
        req.Content.Headers.ContentRange = new ContentRangeHeaderValue(size);
        using var resp = await _http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 308) return (uri, NextOffset(resp) ?? 0);
        TryDelete(sessionPath);
        return (null, 0);
    }

    /// "Range: bytes=0-N" on a 308 means N+1 is the next byte wanted; no
    /// header at all means nothing has landed yet.
    private static long? NextOffset(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("Range", out var values)
        && values.FirstOrDefault()?.Split('-') is [_, var last] && long.TryParse(last, out var n)
            ? n + 1
            : null;

    private async Task<(string? Uri, UploadResult? Error)> StartSessionAsync(
        long size, string title, string description, CancellationToken ct)
    {
        var token = await AccessTokenAsync(ct);
        if (token is null) return (null, new(UploadOutcome.Failed, Error: "authorization broken - run --youtube-auth"));
        using var req = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Upload-Content-Length", size.ToString());
        req.Headers.Add("X-Upload-Content-Type", "video/mp4");
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            snippet = new { title, description, categoryId = "20" }, // 20 = Gaming
            status = new { privacyStatus = Visibility, selfDeclaredMadeForKids = false },
        }), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return (null, Classify(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct)));
        return (resp.Headers.Location?.ToString(), null);
    }

    private UploadResult Classify(HttpStatusCode status, string body)
    {
        // The 1600-unit videos.insert cost against a 10k/day default quota
        // caps automation at ~6 uploads a day; the excess simply queues. The
        // day resets at midnight Pacific - hourly re-checks are cheap enough.
        if (status == HttpStatusCode.Forbidden
            && (body.Contains("quotaExceeded") || body.Contains("dailyLimitExceeded")
                || body.Contains("uploadLimitExceeded") || body.Contains("rateLimitExceeded")))
        {
            _backoffUntilUtc = DateTime.UtcNow.AddHours(1);
            return new(UploadOutcome.Postponed, Error: "YouTube quota/upload limit reached (resets midnight Pacific)");
        }
        if (status == HttpStatusCode.Unauthorized)
        {
            _accessToken = null; // stale token - the next attempt re-mints
            return new(UploadOutcome.Postponed, Error: "access token rejected - refreshing on the next attempt");
        }
        if ((int)status >= 500) return new(UploadOutcome.Postponed, Error: $"YouTube answered {(int)status}");
        return new(UploadOutcome.Failed, Error: $"{(int)status} {Snippet(body)}");
    }

    private async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessTokenExpiresUtc - TimeSpan.FromMinutes(2))
        {
            return _accessToken;
        }
        if (LoadRefreshToken() is not { } refresh)
        {
            _authBroken = true;
            return null;
        }
        using var resp = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.YouTubeClientId,
            ["client_secret"] = config.YouTubeClientSecret,
            ["refresh_token"] = refresh,
            ["grant_type"] = "refresh_token",
        }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // invalid_grant = consent revoked (or the OAuth app left in
            // "testing" mode, whose tokens die after 7 days): deterministic,
            // only a fresh --youtube-auth consent fixes it.
            if (body.Contains("invalid_grant"))
            {
                _authBroken = true;
                Log.Error("YouTube refresh token no longer works (revoked, or the OAuth app is in \"testing\" mode) - run --youtube-auth to re-authorize");
                return null;
            }
            throw new HttpRequestException($"token refresh failed: {(int)resp.StatusCode} {Snippet(body)}");
        }
        var json = JsonDocument.Parse(body).RootElement;
        _accessToken = json.GetProperty("access_token").GetString();
        _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32());
        return _accessToken;
    }

    private string? LoadRefreshToken()
    {
        if (config.YouTubeRefreshToken is { Length: > 0 } fromProfile) return fromProfile;
        try
        {
            return JsonDocument.Parse(File.ReadAllText(TokenPath)).RootElement
                .GetProperty("refresh_token").GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string Snippet(string body) => body.Length <= 300 ? body.ReplaceLineEndings(" ") : body[..300].ReplaceLineEndings(" ");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// The one-time browser consent ("--youtube-auth"): a loopback listener
    /// catches the redirect, the exchanged refresh token lands next to the
    /// exe. Standard installed-app flow (PKCE + state); the resulting grant
    /// is for whichever Google account gets picked in the browser - the
    /// closing log line names the channel so a wrong-account consent is
    /// caught immediately.
    public static async Task<int> AuthorizeAsync(AgentConfig config)
    {
        if (config.YouTubeClientId is not { Length: > 0 } || config.YouTubeClientSecret is not { Length: > 0 })
        {
            Log.Error("Set YouTubeClientId and YouTubeClientSecret in appsettings.json first " +
                      "(Google Cloud Console > Credentials > OAuth client ID, type \"Desktop app\")");
            return 1;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expectedState = Base64Url(RandomNumberGenerator.GetBytes(16));
        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth" +
                      $"?client_id={Uri.EscapeDataString(config.YouTubeClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                      $"&response_type=code&scope={Uri.EscapeDataString(Scope)}" +
                      "&access_type=offline&prompt=consent" +
                      $"&code_challenge={Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))}" +
                      $"&code_challenge_method=S256&state={expectedState}";

        Log.Info("Opening the Google consent page - pick the channel's account and allow YouTube upload access");
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        string? code;
        try
        {
            using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromMinutes(10));
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? "";
            while (await reader.ReadLineAsync() is { Length: > 0 }) { } // drain headers
            var query = ParseQuery(requestLine.Split(' ') is [_, var target, ..] ? target : "");
            code = query.GetValueOrDefault("state") == expectedState ? query.GetValueOrDefault("code") : null;
            var html = code is not null
                ? "<h2>Authorized.</h2>You can close this tab - the agent can now upload to YouTube."
                : $"<h2>Authorization failed.</h2>{query.GetValueOrDefault("error", "no code returned")}";
            var payload = Encoding.UTF8.GetBytes(html);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(payload);
        }
        catch (TimeoutException)
        {
            Log.Error("No consent arrived within 10 minutes - run --youtube-auth again");
            return 1;
        }
        finally
        {
            listener.Stop();
        }
        if (code is null)
        {
            Log.Error("Consent was denied or the redirect was malformed - run --youtube-auth again");
            return 1;
        }

        using var http = new HttpClient();
        using var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.YouTubeClientId,
            ["client_secret"] = config.YouTubeClientSecret,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirect,
        }));
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Log.Error($"Code exchange failed: {(int)resp.StatusCode} {Snippet(body)}");
            return 1;
        }
        var tokens = JsonDocument.Parse(body).RootElement;
        if (tokens.TryGetProperty("refresh_token", out var refreshToken) is false)
        {
            // prompt=consent should always force one; a prior grant lingering
            // at myaccount.google.com/permissions is the known exception.
            Log.Error("Google returned no refresh token - remove the app's earlier grant at myaccount.google.com/permissions and retry");
            return 1;
        }
        File.WriteAllText(TokenPath, JsonSerializer.Serialize(new
        {
            refresh_token = refreshToken.GetString(),
            obtained_utc = DateTime.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true }));

        var channel = await ChannelTitleAsync(http, tokens.GetProperty("access_token").GetString()!);
        Log.Info($"YouTube authorized for channel \"{channel ?? "(unknown)"}\" - token saved to {Path.GetFileName(TokenPath)}");
        return 0;
    }

    private static async Task<string?> ChannelTitleAsync(HttpClient http, string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await http.SendAsync(req);
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement
                .GetProperty("items")[0].GetProperty("snippet").GetProperty("title").GetString();
        }
        catch
        {
            return null; // cosmetic - the grant itself already succeeded
        }
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var query = target.IndexOf('?') is var i and >= 0 ? target[(i + 1)..] : "";
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
