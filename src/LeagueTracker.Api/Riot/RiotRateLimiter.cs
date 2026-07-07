using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Riot;

/// Sliding-window limiter whose windows are (re)configured from Riot's rate-limit
/// response headers, so requests pace to the key's real limits (app + per-method,
/// most restrictive per span) instead of guesses. Keeps a safety margin, pre-seeds
/// usage the server already counts (e.g. another process sharing the key), and
/// tightens the margin if a 429 still slips through.
public sealed class RiotRateLimiter(IOptions<RiotOptions> options)
{
    private readonly record struct Window(int Max, TimeSpan Span);

    private readonly Lock _lock = new();
    private List<Window> _windows = [new(19, TimeSpan.FromSeconds(1)), new(95, TimeSpan.FromSeconds(120))];
    private TimeSpan _widest = TimeSpan.FromSeconds(120);
    private readonly List<DateTime> _history = [];
    private bool _seeded;
    private double _safetyMargin = options.Value.RateSafetyMargin;

    public async Task WaitBudgetAsync(CancellationToken ct)
    {
        while (true)
        {
            int waitMs;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                while (_history.Count > 0 && now - _history[0] >= _widest) _history.RemoveAt(0);

                waitMs = 0;
                foreach (var w in _windows)
                {
                    var windowStart = now - w.Span;
                    var inWindow = 0;
                    for (var i = _history.Count - 1; i >= 0; i--)
                    {
                        if (_history[i] > windowStart) inWindow++; else break;
                    }
                    if (inWindow >= w.Max)
                    {
                        // The oldest request that must age out of this window to free a
                        // slot is its Max-th newest entry; proceed once that is Span old.
                        var freeAt = _history[_history.Count - w.Max] + w.Span;
                        waitMs = Math.Max(waitMs, (int)Math.Ceiling((freeAt - now).TotalMilliseconds));
                    }
                }
                if (waitMs <= 0)
                {
                    _history.Add(now);
                    return;
                }
            }
            await Task.Delay(waitMs, ct);
        }
    }

    public void TightenAfter429() => _safetyMargin = Math.Min(0.25, _safetyMargin + 0.03);

    public void UpdateFromHeaders(HttpResponseMessage resp)
    {
        var appLimit = Parse(Header(resp, "X-App-Rate-Limit"));
        var methodLimit = Parse(Header(resp, "X-Method-Rate-Limit"));
        var appCount = Parse(Header(resp, "X-App-Rate-Limit-Count"));
        if (appLimit.Count == 0) return;

        // Most restrictive cap for each window span across app + method limits.
        var caps = new Dictionary<int, int>();
        foreach (var src in new[] { appLimit, methodLimit })
        {
            foreach (var (span, max) in src)
            {
                if (!caps.TryGetValue(span, out var existing) || max < existing) caps[span] = max;
            }
        }

        lock (_lock)
        {
            _windows = [.. caps.OrderBy(c => c.Key).Select(c =>
                new Window(Math.Max(1, (int)Math.Floor(c.Value * (1 - _safetyMargin))), TimeSpan.FromSeconds(c.Key)))];
            _widest = _windows.Max(w => w.Span);

            if (!_seeded && appCount.Count > 0)
            {
                var widestSpanCount = appCount.OrderByDescending(c => c.Key).First().Value;
                var now = DateTime.UtcNow;
                for (var i = 0; i < widestSpanCount; i++) _history.Add(now);
                _seeded = true;
            }
        }
    }

    private static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// Riot's header format: "value:span,value:span" e.g. "20:1,100:120" -> span => value.
    private static Dictionary<int, int> Parse(string? raw)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var pair in raw.Split(','))
        {
            var kv = pair.Trim().Split(':');
            if (kv.Length == 2 && int.TryParse(kv[0], out var value) && int.TryParse(kv[1], out var span))
            {
                result[span] = value;
            }
        }
        return result;
    }
}
