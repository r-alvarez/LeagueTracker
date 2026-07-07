using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Riot;

public interface IRiotKeyProvider
{
    /// Current API key, or null when none is configured yet.
    string? GetKey();
}

/// Re-reads the key file when it changes on disk, so an expired key can be
/// swapped without restarting the service.
public sealed class RiotKeyProvider(IOptions<RiotOptions> options) : IRiotKeyProvider
{
    private readonly RiotOptions _options = options.Value;
    private string? _cachedKey;
    private DateTime _cachedWriteTimeUtc;

    public string? GetKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey)) return _options.ApiKey.Trim();

        var env = Environment.GetEnvironmentVariable("RIOT_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var file = _options.ApiKeyFile;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;

        var writeTime = File.GetLastWriteTimeUtc(file);
        if (_cachedKey is null || writeTime != _cachedWriteTimeUtc)
        {
            _cachedKey = File.ReadLines(file).FirstOrDefault()?.Trim();
            _cachedWriteTimeUtc = writeTime;
        }
        return string.IsNullOrWhiteSpace(_cachedKey) ? null : _cachedKey;
    }
}
