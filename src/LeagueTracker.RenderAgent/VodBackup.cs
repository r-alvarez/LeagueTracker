using System.Text.Json;

namespace LeagueTracker.RenderAgent;

internal static class VodBackup
{
    internal static async Task<bool> ConfirmAsync(HttpClient http, string api, string matchId, long bytes, JsonElement metadata, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.GetAsync($"{api}/matches/{Uri.EscapeDataString(matchId)}/vod/status?includeApm=false", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            var payload = await Review.ReviewApi.ReadBoundedAsync(response.Content, 2 * 1024 * 1024, timeout.Token);
            using var doc = JsonDocument.Parse(payload);
            return Matches(doc.RootElement, bytes, metadata);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    internal static bool Matches(JsonElement status, long bytes, JsonElement metadata)
    {
        if (bytes <= 0 || status.ValueKind != JsonValueKind.Object || metadata.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("exists", out var exists) || exists.ValueKind != JsonValueKind.True
            || !status.TryGetProperty("sizeBytes", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var remoteBytes) || remoteBytes != bytes
            || !status.TryGetProperty("meta", out var remote) || remote.ValueKind != JsonValueKind.Object) return false;

        foreach (var field in new[] { "matchId", "videoFile", "recordingStartUtc", "recordingEndUtc" })
        {
            if (!metadata.TryGetProperty(field, out var localValue) || localValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(localValue.GetString())
                || !remote.TryGetProperty(field, out var remoteValue) || remoteValue.ValueKind != JsonValueKind.String
                || localValue.GetString() != remoteValue.GetString()) return false;
        }
        return true;
    }
}
