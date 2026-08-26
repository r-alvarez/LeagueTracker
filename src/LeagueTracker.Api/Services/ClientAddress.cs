using System.Net;

namespace LeagueTracker.Api.Services;

// Proxy:ClientIpHeaderFrom - the networks whose CF-Connecting-IP header is
// believed. Cloudflare's published edge ranges unless configured otherwise.
public sealed class ProxyOptions
{
    public List<string> ClientIpHeaderFrom { get; set; } = [.. CloudflareRanges];

    // https://www.cloudflare.com/ips-v4 and /ips-v6 as of 2026-08-26.
    public static readonly string[] CloudflareRanges =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
        "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
        "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
        "2a06:98c0::/29", "2c0f:f248::/32",
    ];
}

public static class ClientAddress
{
    public const string CloudflareHeader = "CF-Connecting-IP";

    public static IReadOnlyList<IPNetwork> ParseNetworks(IEnumerable<string> cidrs) => [.. cidrs.Select(IPNetwork.Parse)];

    // Cloudflare's header names the real client only when the request came
    // through Cloudflare. Believed from any peer, a client could choose its
    // own address: the per-IP enrolment cap and the LastIp an owner
    // recognises a friend's machine by were both forgeable (audit T-N10).
    // The peer here is what the forwarded-headers pass left - the edge that
    // spoke to Traefik - so only Cloudflare's own ranges may hand us one more hop.
    public static IPAddress? FromHeader(IPAddress? peer, string? header, IReadOnlyList<IPNetwork> trusted)
    {
        if (peer is null || !IPAddress.TryParse(header, out var client)) return null;
        var unmapped = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        return trusted.Any(network => network.Contains(unmapped)) ? client : null;
    }
}
