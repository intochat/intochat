using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DigitalBrain.Platform.Links;

// The raw-socket half of a direct link (T20-6): System.Net.Quic owns its socket, so a machine punches from a plain
// UDP socket, closes it, and QUIC re-binds the same port (measured ~61 ms on loopback, Task 20). The same socket
// learns its NAT-reflected endpoint from the trust relay first (R88), or joins a relay slot.
internal static class HolePunch
{
    public static ReadOnlySpan<byte> Magic => "DBPN"u8;
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(50);

    public static Socket Open(int port = 0)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // An ICMP port-unreachable from a peer not punched yet must not fail the next receive.
            const int SioUdpConnreset = unchecked((int)0x9800000C);
            socket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null);
        }
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return socket;
    }

    public static int PortOf(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    // The host's own addresses on the socket's port: what a peer on the same network can try first.
    public static IPEndPoint[] Candidates(int port) =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(unicast => new IPEndPoint(unicast.Address, port)),
    ];

    public static async Task<IPEndPoint> Reflect(Socket socket, IPEndPoint relay, CancellationToken cancellationToken)
    {
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Relay.TokenSize);
        byte[] ask = [.. Relay.ReflectAsk, .. nonce];
        var answer = await Ask(socket, relay, ask, reply => reply.AsSpan().StartsWith(Relay.ReflectAnswer) && reply.AsSpan(4).StartsWith(nonce), cancellationToken);
        var text = Encoding.ASCII.GetString(answer.AsSpan(4 + Relay.TokenSize));
        return IPEndPoint.TryParse(text, out var reflected) ? reflected : throw new InvalidDataException($"The relay reflected '{text}'.");
    }

    public static async Task Join(Socket socket, RelaySlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        byte[] ask = [.. Relay.SlotAsk, .. slot.Token];
        await Ask(socket, slot.Endpoint, ask, reply => reply.AsSpan().StartsWith(Relay.SlotAnswer) && reply.AsSpan(4).SequenceEqual(slot.Token), cancellationToken);
    }

    private static async Task<byte[]> Ask(Socket socket, IPEndPoint to, byte[] ask, Func<byte[], bool> answers, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await socket.SendToAsync(ask, SocketFlags.None, to, cancellationToken);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(Retry);
            try
            {
                while (true)
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token);
                    var reply = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                    if (answers(reply)) { return reply; }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        throw new TimeoutException($"The trust relay at {to} did not answer.");
    }

    // Sends Magic‖nonce to every endpoint of the peer for up to `duration` and returns the first endpoint a punch
    // carrying the same nonce came from, or null. Packets without the nonce are ignored (R88).
    public static async Task<IPEndPoint?> Punch(Socket socket, IPEndPoint[] peer, byte[] nonce, TimeSpan duration, CancellationToken cancellationToken)
    {
        byte[] punch = [.. Magic, .. nonce];
        IPEndPoint? heard = null;
        var after = 0;
        using var over = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        over.CancelAfter(duration);
        var listening = Task.Run(async () =>
        {
            var buffer = new byte[2048];
            try
            {
                while (heard is null)
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), over.Token);
                    if (buffer.AsSpan(0, received.ReceivedBytes).SequenceEqual(punch)) { heard = (IPEndPoint)received.RemoteEndPoint; }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        try
        {
            // Keep punching a few rounds after hearing the peer, so its mapping towards us is open too.
            while (!over.IsCancellationRequested && (heard is null || after++ < 3))
            {
                foreach (var endpoint in peer)
                {
                    try { await socket.SendToAsync(punch, SocketFlags.None, endpoint, over.Token); }
                    catch (SocketException) { }
                }
                await Task.Delay(Every, over.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        await over.CancelAsync();
        await listening;
        return heard;
    }
}
