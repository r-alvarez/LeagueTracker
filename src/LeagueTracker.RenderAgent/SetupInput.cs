using System.Text.Json;

namespace LeagueTracker.RenderAgent;

public sealed record JoinPaste(string? Server, string? Role, string? Prefix, string? Recordings, string? Code);

// The setup window's rules, out of the form so they run without WinForms.
public static class SetupInput
{
    public const string PlainHttpRefused =
        "Tracker URLs must be https - this machine's key and the YouTube credentials the tracker hands it travel on every call (plain http is only accepted for localhost).";

    public static string? ServerUrlProblem(string text)
    {
        var urls = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (urls is not { Length: > 0 }) return "Tracker URL is required.";
        var parsed = urls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null).ToList();
        if (parsed.Any(u => u is null)) return "Tracker URL must be one or more http(s) addresses.";
        return parsed.All(u => u!.Scheme is "https" || IsLoopback(u.Host)) ? null : PlainHttpRefused;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]" or "::1";

    public static string Host(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.Host : url.Trim();

    // lt1: is the older shape; its address and role still apply, its token
    // fields are ignored.
    public static bool IsPaste(string text) =>
        text.StartsWith("lt2:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("lt1:", StringComparison.OrdinalIgnoreCase);

    public static JoinPaste? ParsePaste(string text)
    {
        // Whitespace anywhere, not just at the ends: a one-line paste that
        // travelled through mail or chat comes back wrapped, and a blob with a
        // newline in the middle of it is not base64 any more.
        var code = new string([.. text.Where(c => !char.IsWhiteSpace(c))]);
        if (!IsPaste(code)) return null;
        var b64 = code[4..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
        var root = doc.RootElement;
        string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;
        return new JoinPaste(NonEmpty(Get("server")), NonEmpty(Get("role")), Get("prefix"), NonEmpty(Get("recordings")), NonEmpty(Get("code")));
    }

    private static string? NonEmpty(string? value) => value is { Length: > 0 } ? value : null;

    // K7Q29DFM -> K7Q2-9DFM, the way the Data page shows it.
    public static string Pretty(string code) => code.Length == 8 ? $"{code[..4]}-{code[4..]}" : code;
}
