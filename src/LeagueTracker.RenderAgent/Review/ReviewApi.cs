using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LeagueTracker.RenderAgent.Review;

public sealed record ReviewAccount(string Id, string Server, string AccountId, string Region, string Slug, string Label, string RiotId);

/// A read-only capability, not an arbitrary URL proxy. A frontend request names
/// a discovered account plus one fixed review resource. Keys never enter JS.
public sealed partial class ReviewApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly string[] _servers;
    private readonly ReviewCache _cache;
    private readonly Dictionary<string, ReviewAccount> _accounts = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _requests = new(4);
    private readonly string _identity;
    public ReviewApi(AgentConfig config, string cacheRoot, HttpMessageHandler? handler = null, string? key = null)
    {
        key ??= File.Exists(AgentKey.Path) ? File.ReadAllText(AgentKey.Path).Trim() : "";
        _identity = RecordingLibrary.IdFor(key);
        _servers = config.ServerUrls.Where(ValidServer).ToArray();
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(10) };
        if (key.Length > 0) _http.DefaultRequestHeaders.Add("X-Agent-Key", key);
        _cache = new ReviewCache(Path.Combine(cacheRoot, _identity));
    }

    private static bool ValidServer(string server) => Uri.TryCreate(server, UriKind.Absolute, out var uri)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback));

    public async Task<object> DiscoverAsync(bool refresh, CancellationToken ct)
    {
        var found = new List<ReviewAccount>();
        var offline = false;
        var denied = false;
        foreach (var server in _servers)
        {
            var reply = await FetchAsync(server + "/api/agent/accounts", refresh, ct);
            offline |= reply.Offline || reply.Status == 503;
            denied |= reply.Status is 401 or 403;
            if (reply.Status != 200) continue;
            try
            {
                var root = JsonNode.Parse(reply.Body);
                foreach (var node in root?["accounts"]?.AsArray() ?? [])
                {
                    if (node is null) continue;
                    var id = node["id"]?.GetValue<string>();
                    var region = node["region"]?.GetValue<string>();
                    var slug = node["slug"]?.GetValue<string>();
                    if (id is null || region is null || slug is null) continue;
                    found.Add(new(RecordingLibrary.IdFor(server + ":" + id), server, id, region, slug,
                        node["label"]?.GetValue<string>() ?? slug, node["riotId"]?.GetValue<string>() ?? slug));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { offline = true; }
        }
        lock (_gate)
        {
            _accounts.Clear();
            foreach (var a in found) _accounts[a.Id] = a;
        }
        return new { accounts = found, offline, denied };
    }

    public ReviewAccount Account(string id)
    {
        lock (_gate) return _accounts.TryGetValue(id, out var account) ? account : throw new UnauthorizedAccessException("Select an available account first.");
    }

    [GeneratedRegex(@"\A/matches/[A-Za-z0-9_]{1,100}(?:/(?:track|review|gameplan|vod/status|fullgame/status|clips))?\z")]
    private static partial Regex ReviewPath();
    [GeneratedRegex(@"\A/matches/[A-Za-z0-9_]{1,100}/(?:vod(?:/thumb)?|fullgame|clips/[0-9]{1,5})\z")]
    private static partial Regex MediaPath();

    public static bool IsReviewPath(string path) => ReviewPath().IsMatch(path)
        || Regex.IsMatch(path, @"\A/matches\?page=[1-9][0-9]{0,3}&pageSize=(?:20|50)\z");
    public static bool IsMediaPath(string path) => MediaPath().IsMatch(path);
    public Uri UriFor(string accountId, string path, bool media = false)
    {
        if (!(media ? IsMediaPath(path) : IsReviewPath(path))) throw new ArgumentException("This review operation is not supported.");
        var a = Account(accountId);
        return new Uri($"{a.Server}/api/a/{Uri.EscapeDataString(a.Region)}/{Uri.EscapeDataString(a.Slug)}{path}");
    }

    public Task<CachedReply> GetAsync(string accountId, string path, bool refresh, CancellationToken ct) =>
        FetchAsync(UriFor(accountId, path).AbsoluteUri, refresh, ct);

    private async Task<CachedReply> FetchAsync(string url, bool refresh, CancellationToken ct)
    {
        var cached = _cache.Read(url);
        if (!refresh && cached is not null && DateTime.UtcNow - cached.SavedUtc < TimeSpan.FromMinutes(5)) return cached;
        await _requests.WaitAsync(ct);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _cache.Remove(url);
                return new((int)response.StatusCode, "{\"error\":\"This machine no longer has access. Check its enrollment in Settings.\"}", DateTime.UtcNow);
            }
            if ((int)response.StatusCode >= 500) return cached is not null ? cached with { Offline = true } : new(503, "{}", DateTime.UtcNow, Offline: true);
            var body = await ReadBoundedAsync(response.Content, 8 * 1024 * 1024, ct);
            var reply = new CachedReply((int)response.StatusCode, body, DateTime.UtcNow);
            if (response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "application/json") _cache.Write(url, reply);
            return reply;
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or TaskCanceledException) && !ct.IsCancellationRequested)
        { return cached is not null ? cached with { Offline = true } : new(503, "{\"error\":\"Analysis is unavailable. Local recordings still work.\"}", DateTime.UtcNow, Offline: true); }
        finally { _requests.Release(); }
    }

    public async Task<HttpResponseMessage> OpenMediaAsync(string accountId, string path, string? range, bool head, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, UriFor(accountId, path, true));
        if (range is not null && !request.Headers.TryAddWithoutValidation("Range", range)) throw new ArgumentException("Invalid range.");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (head && response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            // Older trackers expose GET-only media routes. Read their headers without buffering the video.
            response.Dispose();
            using var fallback = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
            if (range is not null) fallback.Headers.TryAddWithoutValidation("Range", range);
            return await _http.SendAsync(fallback, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        return response;
    }

    public static async Task<string> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes) throw new IOException("Response is too large.");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + n > maxBytes) throw new IOException("Response is too large.");
            output.Write(buffer, 0, n);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
