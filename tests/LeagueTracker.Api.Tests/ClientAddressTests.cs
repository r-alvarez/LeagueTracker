using System.Net;
using LeagueTracker.Api.Services;
using Microsoft.Extensions.Configuration;

namespace LeagueTracker.Api.Tests;

public class ClientAddressTests
{
    private static readonly IReadOnlyList<IPNetwork> Cloudflare = ClientAddress.ParseNetworks(ProxyOptions.CloudflareRanges);

    [Fact]
    public void The_header_is_believed_from_a_cloudflare_edge()
    {
        var client = ClientAddress.FromHeader(IPAddress.Parse("104.16.1.1"), "203.0.113.5", Cloudflare);
        Assert.Equal(IPAddress.Parse("203.0.113.5"), client);
    }

    [Fact]
    public void A_v4_mapped_v6_peer_is_matched_as_v4()
    {
        var client = ClientAddress.FromHeader(IPAddress.Parse("::ffff:172.71.0.9"), "2001:db8::1", Cloudflare);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), client);
    }

    [Theory]
    [InlineData("192.168.1.10")]   // the LAN, split-horizon DNS - never Cloudflare
    [InlineData("172.18.0.2")]     // a docker network - also not Cloudflare (172.64.0.0/13 is)
    [InlineData("203.0.113.9")]    // a leaked origin reached directly (audit T-N10)
    public void Anyone_else_sending_the_header_is_ignored(string peer)
    {
        Assert.Null(ClientAddress.FromHeader(IPAddress.Parse(peer), "10.0.0.1", Cloudflare));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    public void A_header_that_is_not_an_address_is_ignored(string? header)
    {
        Assert.Null(ClientAddress.FromHeader(IPAddress.Parse("104.16.1.1"), header, Cloudflare));
    }

    [Fact]
    public void A_configured_entry_is_added_to_the_cloudflare_ranges_and_blanks_are_ignored()
    {
        // Behind Traefik on the NAS every peer is the docker gateway, so the
        // deployment names it in the compose - on top of Cloudflare's ranges,
        // which the binder keeps because the option is a pre-filled list.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxy:ClientIpHeaderFrom:0"] = "172.20.0.1/32",
            ["Proxy:ClientIpHeaderFrom:1"] = "",
        }).Build();
        var options = new ProxyOptions();
        config.GetSection("Proxy").Bind(options);
        var trusted = ClientAddress.ParseNetworks(options.ClientIpHeaderFrom);
        Assert.NotNull(ClientAddress.FromHeader(IPAddress.Parse("172.20.0.1"), "203.0.113.5", trusted));
        Assert.NotNull(ClientAddress.FromHeader(IPAddress.Parse("104.16.1.1"), "203.0.113.5", trusted));
        Assert.Null(ClientAddress.FromHeader(IPAddress.Parse("172.20.0.7"), "203.0.113.5", trusted));
    }

    [Fact]
    public void Configured_ranges_replace_the_default()
    {
        var lanOnly = ClientAddress.ParseNetworks(["10.10.40.0/24"]);
        Assert.NotNull(ClientAddress.FromHeader(IPAddress.Parse("10.10.40.1"), "203.0.113.5", lanOnly));
        Assert.Null(ClientAddress.FromHeader(IPAddress.Parse("104.16.1.1"), "203.0.113.5", lanOnly));
    }
}
