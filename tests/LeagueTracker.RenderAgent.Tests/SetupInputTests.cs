using System.Text;
using System.Text.Json;
using LeagueTracker.RenderAgent;

namespace LeagueTracker.RenderAgent.Tests;

public class SetupInputTests
{
    [Theory]
    [InlineData("https://league.example.org")]
    [InlineData("https://league.example.org/, https://other.example.org")]
    [InlineData("http://localhost:5170")]
    [InlineData("http://127.0.0.1:5399")]
    [InlineData("http://[::1]:5399")]
    public void Https_and_loopback_http_are_accepted(string text) =>
        Assert.Null(SetupInput.ServerUrlProblem(text));

    [Theory]
    [InlineData("http://league.example.org")]
    [InlineData("https://league.example.org, http://192.168.1.20:5170")]
    [InlineData("http://LOCALHOST.evil.example")]
    public void Plain_http_off_the_machine_is_refused_with_the_reason(string text) =>
        Assert.Equal(SetupInput.PlainHttpRefused, SetupInput.ServerUrlProblem(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("league.example.org")]
    [InlineData("ftp://league.example.org")]
    [InlineData("https://ok.example.org, not a url")]
    public void Anything_that_is_not_an_http_address_is_refused(string text) =>
        Assert.NotNull(SetupInput.ServerUrlProblem(text));

    [Fact]
    public void Host_is_what_the_person_can_check() =>
        Assert.Equal("league.example.org", SetupInput.Host(" https://league.example.org:8443/some/path "));

    [Fact]
    public void A_bare_code_is_not_a_paste()
    {
        Assert.Null(SetupInput.ParsePaste("K7Q2-9DFM"));
        Assert.False(SetupInput.IsPaste("K7Q29DFM"));
    }

    [Fact]
    public void A_paste_unpacks_every_field_it_carries()
    {
        var paste = SetupInput.ParsePaste(Paste(new { server = "https://league.example.org", code = "K7Q29DFM", role = "recorder", prefix = "Road to Platinum", recordings = @"D:\Videos" }));

        Assert.Equal(new JoinPaste("https://league.example.org", "recorder", "Road to Platinum", @"D:\Videos", "K7Q29DFM"), paste);
    }

    [Fact]
    public void Whitespace_from_a_wrapped_paste_is_ignored()
    {
        var wrapped = Paste(new { server = "https://league.example.org", code = "K7Q29DFM" });
        var half = wrapped.Length / 2;
        var paste = SetupInput.ParsePaste($" {wrapped[..half]}\r\n {wrapped[half..]} ");

        Assert.Equal("https://league.example.org", paste?.Server);
        Assert.Equal("K7Q29DFM", paste?.Code);
    }

    [Fact]
    public void Blank_fields_read_as_absent_so_they_do_not_wipe_the_form()
    {
        var paste = SetupInput.ParsePaste(Paste(new { server = "", role = "", recordings = "", code = "" }));

        Assert.Equal(new JoinPaste(null, null, null, null, null), paste);
    }

    [Fact]
    public void The_older_lt1_shape_still_yields_its_address_and_role()
    {
        var paste = SetupInput.ParsePaste(Paste(new { server = "https://league.example.org", role = "both", token = "ignored" }, "lt1:"));

        Assert.Equal("https://league.example.org", paste?.Server);
        Assert.Equal("both", paste?.Role);
        Assert.Null(paste?.Code);
    }

    [Theory]
    [InlineData("lt2:!!!not-base64")]
    [InlineData("lt2:bm90IGpzb24")]
    public void A_mangled_paste_throws_rather_than_half_applying(string text) =>
        Assert.ThrowsAny<Exception>(() => SetupInput.ParsePaste(text));

    [Fact]
    public void Pretty_only_hyphenates_an_eight_letter_code()
    {
        Assert.Equal("K7Q2-9DFM", SetupInput.Pretty("K7Q29DFM"));
        Assert.Equal("K7Q29DF", SetupInput.Pretty("K7Q29DF"));
    }

    private static string Paste(object payload, string prefix = "lt2:") =>
        prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
