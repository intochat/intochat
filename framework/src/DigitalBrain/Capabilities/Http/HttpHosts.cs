using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DigitalBrain.Capabilities.Http;

/// <summary>The allow-list check every http@1 implementation applies, so a host is judged the same way everywhere.</summary>
public static class HttpHosts
{
    private static readonly IdnMapping Idn = new();

    /// <summary>
    /// Passes when <paramref name="url"/> is http or https and its host is public (never private, loopback, or link-local).
    /// </summary>
    /// <exception cref="HostNotAllowedException">Anything else; its message carries the scheme and host only, never the path or query.</exception>
    public static void RequirePublic(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var host = url.IsAbsoluteUri ? url.IdnHost : string.Empty;
        var web = url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);
        if (!web || host.Length == 0)
        {
            throw new HostNotAllowedException(host.Length == 0 ? "(no host)" : host, url.IsAbsoluteUri ? url.Scheme : null);
        }

        if (IsPrivateOrLoopback(host))
        {
            throw new HostNotAllowedException(host, url.IsAbsoluteUri ? url.Scheme : null);
        }
    }

    /// <summary>
    /// Passes when <paramref name="url"/> is http or https, its host is public (never private, loopback, or link-local),
    /// and its host equals an entry of <paramref name="allowed"/> or matches a <c>*.example.org</c> entry.
    /// Hosts and entries are compared in their IDN (punycode) form, case-insensitively.
    /// </summary>
    /// <exception cref="HostNotAllowedException">Anything else; its message carries the scheme and host only, never the path or query.</exception>
    public static void Require(Uri url, IReadOnlyList<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(allowed);
        RequirePublic(url);

        if (!allowed.Any(entry => Matches(entry, url.IdnHost)))
        {
            throw new HostNotAllowedException(url.IdnHost, url.Scheme);
        }
    }

    /// <summary>Determines whether a host name or IP string is private, loopback, or link-local.</summary>
    public static bool IsPrivateOrLoopback(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var clean = host.Trim('[', ']').TrimEnd('.');
        if (string.Equals(clean, "localhost", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(clean, out var ip))
        {
            return !IsPublic(ip);
        }

        return false;
    }

    /// <summary>
    /// Determines whether an IP address is routable public Internet space: never private, loopback, link-local, multicast,
    /// shared (CGNAT), benchmarking, documentation or transition space; IPv6 only as global unicast (2000::/3).
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 100 && (b[1] & 0xC0) == 64)
                && !(b[0] == 169 && b[1] == 254)
                && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2) || (b[1] == 88 && b[2] == 99)))
                && !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100)))
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        }

        // Global unicast only; 6to4, Teredo/IETF-protocol and documentation allocations are not public.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xE0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x02)
            && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0D && b[3] == 0xB8)))
            && !(b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0);
    }

    /// <summary>
    /// Determines whether an IP address is routable public Internet space or loopback (never private, link-local,
    /// multicast, CGNAT, documentation, or unspecified). Useful for endpoints that permit local loopback servers
    /// or test stubs while blocking private-network and metadata-service SSRF targets.
    /// </summary>
    public static bool IsPublicOrLoopback(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) || IsPublic(address);
    }

    private static bool Matches(string entry, string host)
    {
        if (entry is null)
        {
            return false;
        }

        var wildcard = entry.StartsWith("*.", StringComparison.Ordinal);
        if (Ascii(wildcard ? entry[2..] : entry) is not { } name)
        {
            return false;
        }

        if (wildcard)
        {
            // The suffix needs at least two labels: *.org would allow a whole top-level domain.
            return name.Contains('.', StringComparison.Ordinal)
                && host.Length > name.Length + 1
                && host.EndsWith("." + name, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(name, host, StringComparison.OrdinalIgnoreCase);
    }

    // The entry in IDN form, or null when it is not a well-formed host name.
    private static string? Ascii(string name)
    {
        if (name.Length == 0 || name.Contains('*', StringComparison.Ordinal) || name.Split('.').Any(label => label.Length == 0))
        {
            return null;
        }

        try
        {
            return Idn.GetAscii(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
