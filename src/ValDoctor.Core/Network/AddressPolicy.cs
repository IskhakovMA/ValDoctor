using System.Net;
using System.Net.Sockets;

namespace ValDoctor.Core.Network;

/// <summary>
/// Decides whether an IP address may be probed or pinned. Only globally routable unicast
/// addresses qualify: ValDoctor never connects to, or writes hosts entries for, loopback,
/// private, link-local, documentation, multicast or otherwise reserved ranges.
/// </summary>
public static class AddressPolicy
{
    private static readonly (uint Network, int Prefix)[] ReservedV4 =
    [
        (0x00000000, 8),   // 0.0.0.0/8 "this network"
        (0x0A000000, 8),   // 10.0.0.0/8 private
        (0x64400000, 10),  // 100.64.0.0/10 carrier-grade NAT
        (0x7F000000, 8),   // 127.0.0.0/8 loopback
        (0xA9FE0000, 16),  // 169.254.0.0/16 link-local
        (0xAC100000, 12),  // 172.16.0.0/12 private
        (0xC0000000, 24),  // 192.0.0.0/24 IETF protocol assignments
        (0xC0000200, 24),  // 192.0.2.0/24 documentation
        (0xC0586300, 24),  // 192.88.99.0/24 deprecated 6to4 relay
        (0xC0A80000, 16),  // 192.168.0.0/16 private
        (0xC6120000, 15),  // 198.18.0.0/15 benchmarking
        (0xC6336400, 24),  // 198.51.100.0/24 documentation
        (0xCB007100, 24),  // 203.0.113.0/24 documentation
        (0xE0000000, 4),   // 224.0.0.0/4 multicast
        (0xF0000000, 4),   // 240.0.0.0/4 reserved + broadcast
    ];

    public static bool IsPublicUnicast(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            if (!address.TryWriteBytes(b, out _))
            {
                return false;
            }

            var value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            foreach (var (network, prefix) in ReservedV4)
            {
                var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                if ((value & mask) == network)
                {
                    return false;
                }
            }

            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6 || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                address.IsIPv6Multicast || address.IsIPv6Teredo || address.IsIPv6UniqueLocal)
            {
                return false;
            }

            Span<byte> b = stackalloc byte[16];
            if (!address.TryWriteBytes(b, out _))
            {
                return false;
            }

            // Global unicast is 2000::/3.
            if ((b[0] & 0xE0) != 0x20)
            {
                return false;
            }

            // 2001:db8::/32 documentation.
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
            {
                return false;
            }

            // IETF assignments that IANA marks as not globally reachable: 2001:2::/48 benchmarking and the deprecated
            // 2001:10::/28 ORCHID (Teredo 2001::/32 is rejected above).
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 &&
                ((b[3] == 0x02 && b[4] == 0x00 && b[5] == 0x00) || (b[3] & 0xF0) == 0x10))
            {
                return false;
            }

            // 3fff::/20 documentation (RFC 9637): 3fff:0000:: – 3fff:0fff:ffff:...
            if (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00)
            {
                return false;
            }

            // 2002::/16 6to4 — embeds an IPv4 address, not a stable server address.
            return !(b[0] == 0x20 && b[1] == 0x02);
        }

        return false;
    }

    /// <summary>
    /// Strict textual IP parsing. <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> accepts
    /// shorthand such as "1" or "1.2" for IPv4, which is never what a hosts file means.
    /// </summary>
    public static bool TryParseStrict(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            // Zone indexes (fe80::1%3) are meaningless in hosts entries we care about.
            if (text.Contains('%', StringComparison.Ordinal) || !IPAddress.TryParse(text, out var v6) ||
                v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            address = v6;
            return true;
        }

        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            // Leading zeros are rejected: some parsers treat them as octal.
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0') || !part.All(char.IsAsciiDigit) || int.Parse(part, System.Globalization.CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(text, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        address = v4;
        return true;
    }
}
