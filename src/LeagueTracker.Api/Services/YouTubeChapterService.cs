using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueTracker.Api.Accounts;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Services;

public sealed class SiteOptions
{
    public string Origin { get; set; } = "";
}

public enum ChapterOutcome { Written, Retry, Skipped, NeedsConsent, Rejected, NoCredentials }

// Files-as-truth like the clips: chapters.txt beside youtube.txt says the
// video was dealt with, so a restart or a rerun never rewrites a video twice.
public sealed class YouTubeChapterService(
    AccountContext acct, VodService vods, ReviewReelService reels, AgentRegistry agents, AgentKeyStore keys,
    IHttpClientFactory http, IOptions<SiteOptions> site, ILogger<YouTubeChapterService> log)
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string VideosEndpoint = "https://www.googleapis.com/youtube/v3/videos";
    // A thin reel on a fresh game is usually the timeline still on its way;
    // on an older one it is the game.
    private static readonly TimeSpan ThinReelGrace = TimeSpan.FromDays(1);

    public string? StampPath(string matchId) => vods.TargetPath(matchId, "chapters.txt");

    public bool IsStamped(string matchId) => StampPath(matchId) is { } path && File.Exists(path);

    public async Task<ChapterOutcome> TryWriteAsync(string matchId, DateTime gameEndUtc, string? preferAgentId, CancellationToken ct)
    {
        if (IsStamped(matchId)) return ChapterOutcome.Skipped;
        if (vods.ReadLink(matchId) is not { } url || VideoIdOf(url) is not { } videoId) return ChapterOutcome.Skipped;

        var reel = await reels.GetAsync(matchId, ct);
        if (reel is null) return ChapterOutcome.Retry;
        var description = YouTubeChapters.Describe(reel, ClockMap(matchId), ReviewUrl(matchId));
        if (description is null)
        {
            return DateTime.UtcNow - gameEndUtc > ThinReelGrace
                ? Stamp(matchId, "skipped: fewer than three moments to chapter", ChapterOutcome.Skipped)
                : ChapterOutcome.Retry;
        }
        if (CredentialsFor(preferAgentId) is not { } credentials) return ChapterOutcome.NoCredentials;

        var outcome = await UpdateDescriptionAsync(credentials, videoId, description, ct);
        switch (outcome)
        {
            case ChapterOutcome.Written:
                Stamp(matchId, DateTime.UtcNow.ToString("O"), outcome);
                log.LogInformation("Chapters written to YouTube for {MatchId} ({Count} moments)", Loggable(matchId), reel.Moments.Count);
                break;
            case ChapterOutcome.Rejected:
                Stamp(matchId, "rejected by YouTube (not this channel's video, or gone)", outcome);
                log.LogWarning("YouTube refused the chapters for {MatchId}: not this channel's video, or it is gone", Loggable(matchId));
                break;
            case ChapterOutcome.NeedsConsent:
                log.LogWarning("YouTube chapters need a refresh token consented with the youtube.force-ssl scope: mint one with deploy/youtube-auth.ps1 and replace the stack's YT_*_REFRESH_TOKEN");
                break;
        }
        return outcome;
    }

    // The id comes off the URL; the vod folder rule already rejects anything
    // but [A-Za-z0-9_], and the log line holds to the same rule (CodeQL).
    private static string Loggable(string matchId) => new([.. matchId.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_')]);

    private ChapterOutcome Stamp(string matchId, string note, ChapterOutcome outcome)
    {
        if (StampPath(matchId) is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, note);
        }
        return outcome;
    }

    private List<(double VideoSec, double GameSec)> ClockMap(string matchId)
    {
        if (vods.MetaPath(matchId) is not { } path) return [];
        try { return YouTubeChapters.ClockMapOf(JsonNode.Parse(File.ReadAllText(path))); }
        catch { return []; }
    }

    private string? ReviewUrl(string matchId) =>
        site.Value.Origin.TrimEnd('/') is { Length: > 0 } origin ? $"{origin}/{acct.UrlSegment}/matches/{Uri.EscapeDataString(matchId)}" : null;

    // The video sits on whichever channel the owner's machines upload to: the
    // posting agent's own block (a friend's channel) beats the owner's other
    // machines, which beat the operator's shared channel.
    private (string ClientId, string ClientSecret, string RefreshToken)? CredentialsFor(string? preferAgentId)
    {
        List<string?> candidates = [];
        if (preferAgentId is { Length: > 0 }) candidates.Add(preferAgentId);
        if (acct.Current.OwnerUserId is { Length: > 0 } owner)
        {
            candidates.AddRange(keys.OwnedBy(owner).Where(k => k.Status is AgentKeyStatus.Approved).Select(k => k.Id));
        }
        candidates.Add(null);
        foreach (var id in candidates.Distinct())
        {
            var profile = agents.ProfileFor(id, sharedSecrets: true);
            if (profile.TryGetValue("YouTubeClientId", out var clientId) && clientId is { Length: > 0 }
                && profile.TryGetValue("YouTubeClientSecret", out var secret) && secret is { Length: > 0 }
                && profile.TryGetValue("YouTubeRefreshToken", out var refresh) && refresh is { Length: > 0 })
            {
                return (clientId, secret, refresh);
            }
        }
        return null;
    }

    private async Task<ChapterOutcome> UpdateDescriptionAsync((string ClientId, string ClientSecret, string RefreshToken) credentials, string videoId, string description, CancellationToken ct)
    {
        var client = http.CreateClient(nameof(YouTubeChapterService));
        try
        {
            using var mint = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = credentials.ClientId,
                ["client_secret"] = credentials.ClientSecret,
                ["refresh_token"] = credentials.RefreshToken,
                ["grant_type"] = "refresh_token",
            }), ct);
            if (!mint.IsSuccessStatusCode)
            {
                log.LogWarning("YouTube token refresh failed ({Status}); chapters wait", (int)mint.StatusCode);
                return (int)mint.StatusCode >= 500 ? ChapterOutcome.Retry : ChapterOutcome.NeedsConsent;
            }
            var token = JsonDocument.Parse(await mint.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString();

            // snippet.title and categoryId are mandatory on update, so the
            // current ones ride along unchanged.
            using var get = new HttpRequestMessage(HttpMethod.Get, $"{VideosEndpoint}?part=snippet&id={Uri.EscapeDataString(videoId)}");
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var current = await client.SendAsync(get, ct);
            if (!current.IsSuccessStatusCode) return Classify(current.StatusCode, await current.Content.ReadAsStringAsync(ct));
            var items = JsonDocument.Parse(await current.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0) return ChapterOutcome.Rejected;
            var snippet = items[0].GetProperty("snippet");

            using var put = new HttpRequestMessage(HttpMethod.Put, $"{VideosEndpoint}?part=snippet");
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            put.Content = new StringContent(JsonSerializer.Serialize(new
            {
                id = videoId,
                snippet = new
                {
                    title = snippet.GetProperty("title").GetString(),
                    categoryId = snippet.TryGetProperty("categoryId", out var category) ? category.GetString() : "20",
                    description,
                },
            }), Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(put, ct);
            return resp.IsSuccessStatusCode ? ChapterOutcome.Written : Classify(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("YouTube unreachable while writing chapters: {Message}", ex.Message);
            return ChapterOutcome.Retry;
        }
    }

    private static ChapterOutcome Classify(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.Forbidden && (body.Contains("insufficientPermissions") || body.Contains("insufficient authentication scopes")))
        {
            return ChapterOutcome.NeedsConsent;
        }
        if (status == HttpStatusCode.Forbidden && (body.Contains("quotaExceeded") || body.Contains("dailyLimitExceeded") || body.Contains("rateLimitExceeded")))
        {
            return ChapterOutcome.Retry;
        }
        return status == HttpStatusCode.Unauthorized || (int)status >= 500 ? ChapterOutcome.Retry : ChapterOutcome.Rejected;
    }

    public static string? VideoIdOf(string url) =>
        System.Text.RegularExpressions.Regex.Match(url, @"(?:youtu\.be/|[?&]v=|shorts/)([A-Za-z0-9_-]{11})") is { Success: true } m ? m.Groups[1].Value : null;
}
