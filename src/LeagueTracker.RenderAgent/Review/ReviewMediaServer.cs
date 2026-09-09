using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeagueTracker.RenderAgent.Review;

/// Media only. Kestrel binds port zero directly (no port-selection race or
/// HTTP.sys URL ACL), lives only as long as the viewer, and streams ranges.
public sealed class ReviewMediaServer : IAsyncDisposable
{
    public const string AppOrigin = "https://app.leaguetracker.invalid";
    private readonly WebApplication _app;
    private readonly string _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private readonly HttpClient _art = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _artLimit = new(3);
    private readonly string _artRoot;
    public string Address { get; private set; } = "";

    public ReviewMediaServer(RecordingLibrary library, ReviewApi api, string cacheRoot)
    {
        _artRoot = Path.Combine(cacheRoot, "art");
        Directory.CreateDirectory(_artRoot);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders(); // Never log session capabilities or private media URLs.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxConcurrentConnections = 20;
        });
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            var token = context.Request.Query["token"].ToString();
            var origin = context.Request.Headers.Origin.ToString();
            if (context.Request.Host.Value != new Uri(Address).Authority
                || (origin.Length > 0 && origin != AppOrigin)
                || !ValidToken(token) || context.Request.Method is not ("GET" or "HEAD"))
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers.AccessControlAllowOrigin = AppOrigin;
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            try { await next(context); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException or OperationCanceledException)
            {
                if (!context.Response.HasStarted) context.Response.StatusCode = ex is UnauthorizedAccessException ? 403 : 404;
                else context.Abort();
            }
        });
        _app.MapMethods("/local/{id}", ["GET", "HEAD"], async (string id, HttpContext context) =>
        {
            var stream = library.OpenVideo(id);
            await Results.File(stream, "video/mp4", enableRangeProcessing: true).ExecuteAsync(context);
        });
        _app.MapMethods("/thumb/{id}", ["GET", "HEAD"], async (string id, HttpContext context) =>
        {
            var path = library.ThumbnailPath(library.Find(id));
            await (path is null ? Results.NotFound() : Results.File(path, "image/jpeg")).ExecuteAsync(context);
        });
        _app.MapMethods("/remote/{accountId}", ["GET", "HEAD"], async (string accountId, HttpContext context) =>
        {
            var path = context.Request.Query["path"].ToString();
            using var response = await api.OpenMediaAsync(accountId, path,
                context.Request.Headers.Range.FirstOrDefault(), HttpMethods.IsHead(context.Request.Method), context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable)) return;
            foreach (var header in new[] { "Content-Length", "Content-Range", "Content-Type", "Last-Modified" })
                if (response.Content.Headers.TryGetValues(header, out var values)) context.Response.Headers[header] = values.ToArray();
            foreach (var header in new[] { "Accept-Ranges", "ETag" })
                if (response.Headers.TryGetValues(header, out var values)) context.Response.Headers[header] = values.ToArray();
            if (!HttpMethods.IsHead(context.Request.Method))
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        _app.MapMethods("/art", ["GET", "HEAD"], ServeArtAsync);
    }

    public bool ValidToken(string token) => token.Length == _token.Length
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(_token));
    public async Task StartAsync(CancellationToken ct)
    {
        await _app.StartAsync(ct);
        Address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }
    public string Local(string id) => $"{Address}/local/{id}?token={_token}";
    public string Thumbnail(string id) => $"{Address}/thumb/{id}?token={_token}";
    public string Remote(string account, string path) => $"{Address}/remote/{account}?token={_token}&path={Uri.EscapeDataString(path)}";
    public string ArtPrefix => $"{Address}/art?token={_token}&url=";

    public static bool AllowedArt(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.Host is "ddragon.leagueoflegends.com" or "raw.communitydragon.org"
        && Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".json" or ".png" or ".jpg" or ".webp" or ".svg";

    private async Task ServeArtAsync(HttpContext context)
    {
        var url = context.Request.Query["url"].ToString();
        if (!AllowedArt(url)) { context.Response.StatusCode = 403; return; }
        var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        var file = Path.Combine(_artRoot, RecordingLibrary.IdFor(url) + extension);
        await _artLimit.WaitAsync(context.RequestAborted);
        try
        {
            // Artwork can be reused offline. Revalidate mutable manifests daily.
            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
            {
                try
                {
                    using var reply = await _art.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    reply.EnsureSuccessStatusCode();
                    if (reply.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new IOException("Artwork too large.");
                    await using var stream = await reply.Content.ReadAsStreamAsync(context.RequestAborted);
                    using var bytes = new MemoryStream();
                    var buffer = new byte[16384];
                    int n;
                    while ((n = await stream.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        if (bytes.Length + n > 8 * 1024 * 1024) throw new IOException("Artwork too large.");
                        bytes.Write(buffer, 0, n);
                    }
                    var temp = file + "." + Guid.NewGuid().ToString("n") + ".tmp";
                    try { await File.WriteAllBytesAsync(temp, bytes.ToArray(), context.RequestAborted); File.Move(temp, file, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    ReviewCache.Trim(_artRoot, 128L * 1024 * 1024);
                }
                catch (Exception ex) when (File.Exists(file) && (ex is IOException or HttpRequestException or TaskCanceledException) && !context.RequestAborted.IsCancellationRequested) { }
            }
            var type = extension switch { ".json" => "application/json", ".svg" => "image/svg+xml", ".jpg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
            await Results.File(file, type).ExecuteAsync(context);
        }
        finally { _artLimit.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _app.StopAsync(stop.Token); } catch (OperationCanceledException) { }
        await _app.DisposeAsync();
        _art.Dispose();
        _artLimit.Dispose();
    }
}
