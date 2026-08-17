namespace LeagueTracker.Api.Services;

/// Guards /api/agent/*: the slice Cloudflare Access lets through unauthenticated
/// so agents can enrol with nothing but the URL. Anonymous: enrol, its status
/// poll, ping, and the release feed (public builds anyway). Everything else
/// needs an approved key in X-Agent-Key.
public sealed class AgentAuthMiddleware(RequestDelegate next, AgentKeyStore keys)
{
    public const string HeaderName = "X-Agent-Key";
    public const string ItemKey = "agent-key-record";

    private static readonly string[] Anonymous = ["/api/agent/enroll", "/api/agent/ping", "/api/agent/release"];

    public async Task InvokeAsync(HttpContext http)
    {
        var path = http.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/agent/", StringComparison.OrdinalIgnoreCase)) { await next(http); return; }

        var key = http.Request.Headers[HeaderName].FirstOrDefault();
        var record = key is { Length: >= 16 } ? keys.Find(key) : null;
        if (record is not null && record.Status is AgentKeyStatus.Approved)
        {
            keys.Touch(record, http.Connection.RemoteIpAddress?.ToString());
            http.Items[ItemKey] = record;
            await next(http);
            return;
        }
        if (Anonymous.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
        {
            await next(http);
            return;
        }
        http.Response.StatusCode = record is null ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(record is null
            ? "{\"error\":\"agent key missing or unknown - enrol first\"}"
            : $"{{\"error\":\"agent is {record.Status.ToString().ToLowerInvariant()}\"}}");
    }
}
