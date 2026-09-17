using System.Text.Json.Nodes;

namespace LeagueTracker.Api.Services;

// YouTube's chapter rules are not enforced by the API, only by the player:
// first chapter at 0:00, at least three, ten seconds or more apart - break
// one and the description shows no chapters at all.
public static class YouTubeChapters
{
    public const int MinChapters = 3;
    public const int MinGapSec = 10;

    public static string? Describe(ReviewReelService.Reel reel, IReadOnlyList<(double VideoSec, double GameSec)> clockMap, string? reviewUrl)
    {
        var chapters = new List<(double At, string Label)> { (0, "Loading screen") };
        foreach (var m in reel.Moments.OrderBy(m => m.StartSec))
        {
            var at = Math.Floor(VideoFor(clockMap, m.StartSec));
            if (at - chapters[^1].At < MinGapSec) continue;
            chapters.Add((at, m.Detail is { Length: > 0 } ? $"{m.Title} - {m.Detail}" : m.Title));
        }
        if (chapters.Count < MinChapters) return null;

        // YouTube keeps the newlines it is given; Windows line ends would show up as blank lines.
        var lines = new List<string> { $"Match {reel.MatchId}" };
        if (reviewUrl is { Length: > 0 }) lines.Add($"Review on the tracker: {reviewUrl}");
        lines.Add("");
        lines.AddRange(chapters.Select(c => $"{Clock(c.At)} {c.Label}"));
        return string.Join('\n', lines);
    }

    // The same map the match page seeks with: a capture restart leaves the
    // two sides of a seam at different offsets, so one offset would not do.
    public static double VideoFor(IReadOnlyList<(double VideoSec, double GameSec)> clockMap, double gameSec)
    {
        var pairs = clockMap.OrderBy(p => p.VideoSec).ToList();
        if (pairs is []) return Math.Max(0, gameSec);
        if (gameSec <= pairs[0].GameSec) return Math.Max(0, pairs[0].VideoSec + (gameSec - pairs[0].GameSec));
        var last = pairs[^1];
        if (gameSec >= last.GameSec) return last.VideoSec + (gameSec - last.GameSec);
        var upper = pairs.FindIndex(p => p.GameSec >= gameSec);
        var lo = pairs[upper - 1];
        var hi = pairs[upper];
        var span = hi.GameSec - lo.GameSec;
        return span <= 0 ? lo.VideoSec : lo.VideoSec + (gameSec - lo.GameSec) / span * (hi.VideoSec - lo.VideoSec);
    }

    public static List<(double VideoSec, double GameSec)> ClockMapOf(JsonNode? sidecar) =>
        [.. (sidecar?["clockMap"]?.AsArray() ?? []).OfType<JsonNode>()
            .Select(n => (n["videoSec"]?.GetValue<double>() ?? 0, n["gameSec"]?.GetValue<double>() ?? 0))];

    public static string Clock(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(sec)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
