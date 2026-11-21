using System.Net;
using System.Net.Sockets;

namespace DigitalBrain.Capabilities.Http;

// The handler's connect: resolves the host, keeps only public addresses (never loopback, link-local, private, unique-local,
// unspecified or multicast, IPv4-mapped included) and connects to the first that answers. A host with none is refused before any
// socket opens, so an allowed name that resolves inward cannot reach the host's own network.
public sealed class VettedConnect(VettedConnect.Resolver resolve)
{
    public delegate Task<IPAddress[]> Resolver(string host, CancellationToken ct);

    public static readonly Resolver Dns = static (host, ct) => System.Net.Dns.GetHostAddressesAsync(host, ct);

    public async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await resolve(host, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in Public(host, addresses))
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

    // The public addresses of host, in resolver order; none is a refusal.
    public static IPAddress[] Public(string host, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var vetted = addresses.Where(IsPublic).ToArray();
        return vetted.Length > 0 ? vetted : throw new HostNotAllowedException(host);
    }

    public static bool IsPublic(IPAddress address) => HttpHosts.IsPublic(address);
}
