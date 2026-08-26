using LeagueTracker.Api.Auth;

namespace LeagueTracker.Api.Tests;

// Where the login flow may send a person afterwards: this site only.
public class AuthReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/euw/ImRA-87166/matches")]
    [InlineData("/euw/ImRA-87166/matches?queue=ranked&x=1")]
    public void Same_site_paths_pass_through(string returnUrl) => Assert.Equal(returnUrl, AuthEndpoints.SafeReturn(returnUrl));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.com/")]
    [InlineData("//evil.com")]
    [InlineData(@"/\evil.com")]       // browsers resolve this to https://evil.com (audit T-N3)
    [InlineData(@"/\\evil.com")]
    [InlineData("evil.com")]
    [InlineData("/ok\r\nLocation: https://evil.com")]
    public void Anything_that_could_leave_the_site_falls_back_to_the_root(string? returnUrl) => Assert.Equal("/", AuthEndpoints.SafeReturn(returnUrl));
}
