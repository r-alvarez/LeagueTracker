using LeagueTracker.Api.Accounts;

namespace LeagueTracker.Api.Tests;

/// The URL code <-> Riot platform table every account address goes through.
public class PlatformsTests
{
    [Theory]
    [InlineData("euw", "euw1")]
    [InlineData("EUW", "euw1")]
    [InlineData("na", "na1")]
    [InlineData("kr", "kr")]
    public void ByCode_finds_the_platform_case_insensitively(string code, string platform)
        => Assert.Equal(platform, Platforms.ByCode(code)?.Platform);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mars")]
    public void ByCode_is_null_for_unknown_codes(string? code)
        => Assert.Null(Platforms.ByCode(code));

    [Fact]
    public void Codes_and_platforms_are_unique_and_round_trip()
    {
        Assert.Equal(Platforms.All.Count, Platforms.All.Select(e => e.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Platforms.All.Count, Platforms.All.Select(e => e.Platform).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var e in Platforms.All) Assert.Equal(e.Code, Platforms.CodeFor(e.Platform));
    }

    [Fact]
    public void CodeFor_unknown_platform_falls_back_to_the_lowercased_id()
        => Assert.Equal("xx9", Platforms.CodeFor("XX9"));
}
