namespace LeagueTracker.Api.Accounts;

/// Riot's three names for a place: the platform (league-v4, spectator:
/// euw1), the regional route (account-v1, match-v5: europe) and the short
/// code people use in URLs (op.gg's /euw/). One table, so a URL region and
/// a platform id never drift apart.
public static class Platforms
{
    public sealed record Entry(string Code, string Platform, string Region, string Label);

    public static readonly IReadOnlyList<Entry> All =
    [
        new("euw", "euw1", "europe", "EU West"),
        new("eune", "eun1", "europe", "EU Nordic & East"),
        new("tr", "tr1", "europe", "Türkiye"),
        new("ru", "ru", "europe", "Russia"),
        new("me", "me1", "europe", "Middle East"),
        new("na", "na1", "americas", "North America"),
        new("br", "br1", "americas", "Brazil"),
        new("lan", "la1", "americas", "Latin America North"),
        new("las", "la2", "americas", "Latin America South"),
        new("kr", "kr", "asia", "Korea"),
        new("jp", "jp1", "asia", "Japan"),
        new("oce", "oc1", "sea", "Oceania"),
        new("ph", "ph2", "sea", "Philippines"),
        new("sg", "sg2", "sea", "Singapore"),
        new("th", "th2", "sea", "Thailand"),
        new("tw", "tw2", "sea", "Taiwan"),
        new("vn", "vn2", "sea", "Vietnam"),
    ];

    public static Entry? ByCode(string? code) =>
        code is { Length: > 0 } ? All.FirstOrDefault(e => e.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) : null;

    public static Entry? ByPlatform(string? platform) =>
        platform is { Length: > 0 } ? All.FirstOrDefault(e => e.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)) : null;

    /// The URL code for a platform id; unknown platforms fall back to the id
    /// itself so a misconfigured account is still addressable.
    public static string CodeFor(string platform) => ByPlatform(platform)?.Code ?? platform.ToLowerInvariant();
}
