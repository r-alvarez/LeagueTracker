using LeagueTracker.Api.Accounts;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

/// Set via env: RenderWatch__StallMinutes=120, RenderWatch__NtfyUrl=https://ntfy.sh/<topic>
public sealed class RenderWatchOptions
{
    /// How long render work may wait with nothing finishing before the
    /// operator hears about it. A full-game render only reports at the end
    /// (about the game's length plus the upload), and a woken box needs a few
    /// minutes to launch League, so this stays well past an hour.
    public int StallMinutes { get; set; } = 120;

    /// Optional phone push: an ntfy topic URL (ntfy.sh or self-hosted). Blank
    /// = the alert shows only on the site and in admins' agent trays.
    public string NtfyUrl { get; set; } = "";

    /// While a stall lasts, push again this often.
    public int RemindHours { get; set; } = 6;
}

/// When each account's render work last moved: a window uploaded, a job
/// completed, failed or proved unrenderable, or a renderer asked for work and
/// got none (it is awake and caught up). In memory - a restart just starts
/// every clock afresh.
public sealed class RenderProgress
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _last = new(StringComparer.Ordinal);

    public void Touch(string accountId)
    {
        lock (_gate) _last[accountId] = DateTime.UtcNow;
    }

    public DateTime? LastUtc(string accountId)
    {
        lock (_gate) return _last.TryGetValue(accountId, out var at) ? at : null;
    }
}

/// Id changes when a new stall starts, so an agent tray shows each stall
/// once rather than on every heartbeat.
public sealed record RenderAlert(string Id, int Pending, string[] Accounts, DateTime SinceUtc, string Cause, string Message);

public sealed record RenderStall(string AccountId, int Pending, DateTime SinceUtc);

/// Watches for the failure nobody sees: render jobs waiting while the render
/// box does nothing (asleep and not woken, off, signed out, agent dead,
/// paused, League will not launch). Nothing errors in that state - the queue
/// just grows - so the tracker, which sees both the queue and the renderers'
/// heartbeats, says so: on the site for admins, in admins' agent trays via
/// the heartbeat, and as an optional ntfy push.
public sealed class RenderWatchdog(
    RenderPendingCount pending, RenderProgress progress, AgentRegistry agents, AccountRegistry accounts,
    IOptions<RenderWatchOptions> options, IHttpClientFactory http, ILogger<RenderWatchdog> log) : BackgroundService
{
    public const string HttpClientName = "render-watch";
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    // Per account: when its current backlog was first seen (dropped when it empties).
    private readonly Dictionary<string, DateTime> _backlogSince = new(StringComparer.Ordinal);
    private RenderAlert? _current;
    private DateTime _lastPushUtc;

    public RenderAlert? Current
    {
        get { lock (_gate) return _current; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the renderers' first heartbeats land before judging them.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await CheckAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Render watch pass failed: {Message}", ex.Message); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// The accounts whose waiting work has not moved for threshold. Movement
    /// is the later of "the backlog appeared" and "work last moved", so work
    /// that queues onto an idle-but-healthy renderer gets the full threshold.
    public static List<RenderStall> FindStalls(IReadOnlyDictionary<string, int> pendingByAccount,
        Dictionary<string, DateTime> backlogSince, Func<string, DateTime?> lastProgress, DateTime now, TimeSpan threshold)
    {
        foreach (var emptied in backlogSince.Keys.Where(id => !pendingByAccount.ContainsKey(id)).ToList()) backlogSince.Remove(emptied);
        var stalls = new List<RenderStall>();
        foreach (var (accountId, count) in pendingByAccount)
        {
            if (!backlogSince.TryGetValue(accountId, out var since)) backlogSince[accountId] = since = now;
            var moved = lastProgress(accountId) is { } last && last > since ? last : since;
            if (now - moved >= threshold) stalls.Add(new RenderStall(accountId, count, moved));
        }
        return stalls;
    }

    /// The likeliest reason, from what the renderers last said about themselves.
    public static string Cause(IReadOnlyList<AgentLive> renderers, DateTime now)
    {
        if (renderers is not { Count: > 0 }) return "No render machine has reported since the tracker started - is its agent running?";
        var online = renderers.Where(r => r.Online).ToList();
        if (online is not { Count: > 0 })
        {
            var newest = renderers.MaxBy(r => r.SeenUtc)!;
            return $"{newest.Agent} last reported {Ago(newest.SeenUtc, now)} ago - asleep and not woken, switched off, signed out, or its agent has stopped.";
        }
        if (online.All(r => r.Paused)) return $"{string.Join(", ", online.Select(r => r.Agent))} is paused from its tray icon.";
        var working = online.First(r => !r.Paused);
        var doing = working.Detail is { Length: > 0 } ? $"{working.State}: {working.Detail}" : working.State;
        var error = working.LastError is { Length: > 0 } e ? $", last error: {e}" : "";
        return $"{working.Agent} is online but not finishing jobs - it reports \"{doing}\"{error}.";
    }

    public static string Ago(DateTime sinceUtc, DateTime now)
    {
        var span = now - sinceUtc;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, span.Minutes)}m";
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var pendingByAccount = await pending.ByAccountAsync(ct);
        var now = DateTime.UtcNow;
        var threshold = TimeSpan.FromMinutes(Math.Max(15, options.Value.StallMinutes));
        List<RenderStall> stalls;
        lock (_gate) stalls = FindStalls(pendingByAccount, _backlogSince, progress.LastUtc, now, threshold);

        if (stalls is not { Count: > 0 })
        {
            RenderAlert? cleared;
            lock (_gate) (cleared, _current) = (_current, null);
            if (cleared is null) return;
            log.LogInformation("Render queue moving again (stalled since {Since:u})", cleared.SinceUtc);
            await PushAsync("Renders moving again", $"The render queue is moving again after {Ago(cleared.SinceUtc, now)}.", "white_check_mark", ct);
            return;
        }

        var sinceUtc = stalls.Min(s => s.SinceUtc);
        var total = stalls.Sum(s => s.Pending);
        var names = stalls.Select(s => accounts.ById(s.AccountId)?.RiotId ?? s.AccountId).ToArray();
        var cause = Cause(agents.Renderers(), now);
        var message = $"{total} render job{(total is 1 ? "" : "s")} waiting ({string.Join(", ", names)}) and nothing has finished for {Ago(sinceUtc, now)}. {cause}";

        bool fresh, remind;
        lock (_gate)
        {
            fresh = _current is null;
            _current = new RenderAlert(_current?.Id ?? sinceUtc.ToString("yyyyMMddHHmmss"), total, names, sinceUtc, cause, message);
            remind = !fresh && now - _lastPushUtc >= TimeSpan.FromHours(Math.Max(1, options.Value.RemindHours));
        }
        if (fresh) log.LogWarning("Render queue stalled: {Message}", message);
        if (fresh || remind) await PushAsync("Render box stalled", message, "warning", ct);
    }

    private async Task PushAsync(string title, string body, string tag, CancellationToken ct)
    {
        lock (_gate) _lastPushUtc = DateTime.UtcNow;
        if (options.Value.NtfyUrl is not { Length: > 0 } url) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body) };
            request.Headers.Add("Title", title);
            request.Headers.Add("Tags", tag);
            using var resp = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode) log.LogWarning("Render watch push answered {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Render watch push failed: {Message}", ex.Message);
        }
    }
}
