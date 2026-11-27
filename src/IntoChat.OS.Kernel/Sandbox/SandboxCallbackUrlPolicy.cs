using System.Net;
using System.Net.Sockets;
using DigitalBrain.Capabilities.Http;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Validates and normalizes remote <see cref="SandboxOptions.KernelCallbackUrl"/> targets before the
/// parent Kernel forwards pipe callbacks over HTTP, preventing SSRF and DNS-rebinding pivots from a
/// confined <c>NetworkPolicy.None</c> child process to loopback, link-local/metadata, or private networks.
/// </summary>
internal static class SandboxCallbackUrlPolicy
{
    /// <summary>
    /// Validates <paramref name="rawUrl"/> and normalizes it to a canonical <c>scheme://authority</c> origin
    /// (stripping any path, query, or fragment).
    /// </summary>
    public static bool TryValidateAndNormalize(
        string? rawUrl,
        bool allowLoopback,
        out string canonicalOrigin,
        out string? rejectionReason)
    {
        canonicalOrigin = string.Empty;
        rejectionReason = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            rejectionReason = "KernelCallbackUrl is empty.";
            return false;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            rejectionReason = "KernelCallbackUrl must be a valid absolute URI.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            rejectionReason = "KernelCallbackUrl must not contain embedded userinfo credentials.";
            return false;
        }

        var scheme = uri.Scheme;
        var isHttp = string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var isHttps = string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !isHttps)
        {
            rejectionReason = $"KernelCallbackUrl scheme '{scheme}' is not allowed.";
            return false;
        }

        var host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            rejectionReason = "KernelCallbackUrl must specify a host.";
            return false;
        }

        var cleanHost = host.Trim('[', ']').TrimEnd('.');
        if (IsLoopbackHost(cleanHost))
        {
            if (!allowLoopback)
            {
                rejectionReason = $"Loopback KernelCallbackUrl host '{cleanHost}' is blocked unless SandboxOptions.AllowLoopbackKernelCallback is enabled.";
                return false;
            }

            canonicalOrigin = $"{scheme.ToLowerInvariant()}://{uri.Authority}";
            return true;
        }

        if (!isHttps)
        {
            rejectionReason = $"Non-loopback KernelCallbackUrl must use https:// (rejected '{scheme}').";
            return false;
        }

        if (HttpHosts.IsPrivateOrLoopback(cleanHost))
        {
            rejectionReason = $"KernelCallbackUrl host '{cleanHost}' is in a private, link-local, metadata, or reserved network range.";
            return false;
        }

        canonicalOrigin = $"{Uri.UriSchemeHttps}://{uri.Authority}";
        return true;
    }

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> that disables redirects and vets every resolved IP address
    /// at TCP connect time to prevent DNS rebinding into loopback, link-local/metadata, or RFC1918/ULA ranges.
    /// </summary>
    public static SocketsHttpHandler CreateRemoteCallbackHandler(
        bool allowLoopback,
        VettedConnect.Resolver? resolver = null)
    {
        var dnsResolver = resolver ?? VettedConnect.Dns;
        var publicConnect = new VettedConnect(dnsResolver);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = allowLoopback
                ? (context, ct) => ConnectAllowingLoopbackAsync(context, dnsResolver, ct)
                : publicConnect.Connect,
        };
    }

    internal static IPAddress[] FilterAllowedAddresses(string host, IReadOnlyList<IPAddress> addresses, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (!allowLoopback)
        {
            return VettedConnect.Public(host, addresses);
        }

        var vetted = addresses
            .Where(static ip => HttpHosts.IsPublic(ip) || IsLoopbackAddress(ip))
            .ToArray();
        return vetted.Length > 0 ? vetted : throw new HostNotAllowedException(host);
    }

    private static async ValueTask<Stream> ConnectAllowingLoopbackAsync(
        SocketsHttpConnectionContext context,
        VettedConnect.Resolver resolver,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await resolver(host, ct).ConfigureAwait(false);

        Exception? last = null;
        foreach (var address in FilterAllowedAddresses(host, addresses, allowLoopback: true))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error)
            {
                socket.Dispose();
                last = error;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"Could not connect to {host}.", last);
    }

    private static bool IsLoopbackHost(string cleanHost)
    {
        if (string.Equals(cleanHost, "localhost", StringComparison.OrdinalIgnoreCase)
            || cleanHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(cleanHost, out var ip) && IsLoopbackAddress(ip);
    }

    private static bool IsLoopbackAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }
}
