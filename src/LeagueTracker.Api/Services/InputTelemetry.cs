namespace LeagueTracker.Telemetry;

/// Reads one game's input telemetry (events.csv.gz rows) into what the review
/// UI draws: the APM line and the ult casts. Shared source between the API
/// and the render agent (linked into both) so the site and the desktop review
/// window can never disagree about the same file.
///
/// APM counts presses, not key_down rows: Windows repeats key_down ~30 times
/// a second while a key is held, so holding Tab for the scoreboard used to
/// read as a burst of hundreds of "actions" (measured against Ascent on the
/// same game: identical clicks and presses, 1,101 extra repeat rows).
public sealed class InputTelemetry
{
    public const int BucketSec = 10;

    /// Bumped whenever the derived series changes meaning, so cached apm.json
    /// files from an older reading are recomputed rather than trusted.
    public const int Version = 2;

    // Presses of the ult key this close together are one use of the ult:
    // recast ults (Ahri's three dashes, a spam of R while the cast lands)
    // must not read as several.
    private const double UltChainGapSec = 15;

    // A repeat keeps arriving every ~33ms after the first ~250-1000ms; a
    // key_down on a "held" key after longer than this is a fresh press whose
    // key_up was lost while the game was not foreground.
    private const long RepeatWindowMs = 1100;

    private readonly List<int> _buckets = [];
    private readonly Dictionary<string, long> _held = new(StringComparer.Ordinal);
    private readonly List<double> _ultPresses = [];
    private readonly List<(double Sec, int Lum)> _ultSlot = [];

    public IReadOnlyList<int> Buckets => _buckets;

    /// Feeds one row; tMs is already validated by the caller.
    public void Add(long tMs, string type, string name, string valueA)
    {
        switch (type)
        {
            case "key_down":
                if (_held.TryGetValue(name, out var last) && tMs - last < RepeatWindowMs)
                {
                    _held[name] = tMs;
                    return;
                }
                _held[name] = tMs;
                // Ctrl+R levels the ult up; it casts nothing.
                if (name == "R" && !_held.ContainsKey("ctrl")) _ultPresses.Add(tMs / 1000.0);
                break;
            case "key_up":
                _held.Remove(name);
                return;
            case "mouse_down" or "wheel":
                break;
            case "hud_r":
                if (int.TryParse(valueA, out var lum)) _ultSlot.Add((tMs / 1000.0, lum));
                return;
            default:
                return;
        }
        var bucket = (int)(tMs / 1000 / BucketSec);
        while (_buckets.Count <= bucket) _buckets.Add(0);
        _buckets[bucket]++;
    }

    public UltCast[] Ults(out string? source)
    {
        var chains = new List<(double First, double Last, int Presses)>();
        foreach (var t in _ultPresses.Order())
        {
            if (chains.Count > 0 && t - chains[^1].Last <= UltChainGapSec) chains[^1] = (chains[^1].First, t, chains[^1].Presses + 1);
            else chains.Add((t, t, 1));
        }
        source = chains.Count == 0 ? null : "keys";
        if (chains.Count == 0) return [];

        // With the recorder's samples of the R slot, a chain is a cast only
        // if the slot actually went on cooldown right after it - pressing R
        // with the ult down, or out of range, casts nothing.
        var cooldowns = CooldownStarts();
        var confirmed = chains.Where(c => cooldowns.Any(s => s >= c.First - 1 && s <= c.Last + 3)).ToList();
        if (cooldowns.Count > 0 && confirmed.Count > 0)
        {
            source = "hud";
            return confirmed.Select(c => new UltCast(Math.Round(c.First, 1), c.Presses, true)).ToArray();
        }
        return chains.Select(c => new UltCast(Math.Round(c.First, 1), c.Presses, false)).ToArray();
    }

    /// Where the R slot went from ready to at least four seconds dark. Ready
    /// is relative to the brightest the slot usually gets (icons differ per
    /// champion); the cooldown veil takes it to ~65% of that. Shorter dips are
    /// crowd control and recast lockouts, not a cooldown.
    private List<double> CooldownStarts()
    {
        var starts = new List<double>();
        if (_ultSlot.Count < 20) return starts;
        var sorted = _ultSlot.Select(s => s.Lum).Order().ToArray();
        var readyLevel = sorted[(int)(sorted.Length * 0.9)];
        if (readyLevel < 40) return starts;
        var threshold = readyLevel * 0.8;

        var wasReady = false;
        double? darkSince = null;
        foreach (var (sec, lum) in _ultSlot.OrderBy(s => s.Sec))
        {
            if (lum >= threshold)
            {
                wasReady = true;
                darkSince = null;
                continue;
            }
            if (!wasReady) continue;
            darkSince ??= sec;
            if (sec - darkSince.Value >= 4)
            {
                starts.Add(darkSince.Value);
                wasReady = false;
                darkSince = null;
            }
        }
        return starts;
    }

    public object Series()
    {
        var ults = Ults(out var source);
        return new
        {
            v = Version,
            bucketSec = BucketSec,
            // counts-per-bucket scaled to per-minute, the unit players know
            apm = _buckets.Select(c => c * 60 / BucketSec).ToArray(),
            averageApm = _buckets is { Count: > 0 } ? (int)Math.Round(_buckets.Sum() * 60.0 / (_buckets.Count * BucketSec)) : 0,
            // Spelled out: neither caller's serializer camel-cases records.
            ults = ults.Select(u => new { videoSec = u.VideoSec, presses = u.Presses, confirmed = u.Confirmed }).ToArray(),
            ultSource = source,
        };
    }
}

/// One use of the ult on the video's clock. Confirmed = the R slot was seen
/// going on cooldown; otherwise it is only a chain of R presses.
public sealed record UltCast(double VideoSec, int Presses, bool Confirmed);
