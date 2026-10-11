using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Platform.Links;

// A relay slot a machine registers its relayed port with (T20-5): the relay endpoint and the slot's token.
[GenerateSerializer, Alias("platform.links.relay-slot")]
internal sealed record RelaySlot([property: Id(0)] IPEndPoint Endpoint, [property: Id(1)] byte[] Token);

// The trust host's UDP relay (T20-5, Q24): a datagram forwarder, never a TLS end. The QUIC session runs end to end
// between the two machines, so the relay sees only QUIC packets (ciphertext and connection ids), the two machine
// ids of a slot, the endpoints, sizes and timing. The same port answers reflection requests (T20-6, R88): a
// punching socket learns its NAT-mapped endpoint as the relay sees it.
//
// Datagrams from an endpoint registered in a slot are forwarded to the slot's other endpoint. Anything else is
// control: `DBRF`+nonce → `DBRR`+nonce+"address:port"; `DBRS`+token → registers the sender in that slot (two
// endpoints at most) → `DBRA`+token. Unknown tokens and strangers get no answer.
internal sealed class Relay : IAsyncDisposable
{
    public static readonly TimeSpan SlotIdle = TimeSpan.FromSeconds(60);
    public const int TokenSize = 16;
    private const int Receivers = 8;
    private const int SocketBuffer = 4 * 1024 * 1024;

    public static ReadOnlySpan<byte> ReflectAsk => "DBRF"u8;
    public static ReadOnlySpan<byte> ReflectAnswer => "DBRR"u8;
    public static ReadOnlySpan<byte> SlotAsk => "DBRS"u8;
    public static ReadOnlySpan<byte> SlotAnswer => "DBRA"u8;

    private readonly Socket _socket;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task[] _receiving;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<IPEndPoint, Slot> _routes = new();

    private Relay(Socket socket, TimeProvider time)
    {
        (_socket, _time) = (socket, time);
        _receiving = [.. Enumerable.Range(0, Receivers).Select(_ => Task.Run(Receive))];
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    // Facts only: flips one bit in every forwarded datagram while set, and sees each forwarded datagram.
    internal bool Tamper { get; set; }

    internal Action<byte[]>? Forwarded { get; set; }

    public static Relay Start(IPEndPoint at, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(at);
        var socket = new Socket(at.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = SocketBuffer, SendBufferSize = SocketBuffer };
        if (OperatingSystem.IsWindows())
        {
            // An ICMP port-unreachable from a gone peer must not fail the relay's next receive.
            const int SioUdpConnreset = unchecked((int)0x9800000C);
            socket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null);
        }
        socket.Bind(at);
        return new Relay(socket, time);
    }

    public byte[] Allocate(string machineA, string machineB)
    {
        Expire();
        var token = RandomNumberGenerator.GetBytes(TokenSize);
        _slots[Convert.ToHexString(token)] = new Slot(machineA, machineB, _time.GetUtcNow());
        return token;
    }

    private async Task Receive()
    {
        var buffer = new byte[65_536];
        EndPoint any = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_stopping.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try { received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stopping.Token).ConfigureAwait(false); }
            catch (Exception stopped) when (stopped is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            var from = (IPEndPoint)received.RemoteEndPoint;
            var datagram = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
            try { await Handle(from, datagram).ConfigureAwait(false); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    private async Task Handle(IPEndPoint from, byte[] datagram)
    {
        if (_routes.TryGetValue(from, out var slot) && slot.Other(from) is { } to)
        {
            slot.Seen = _time.GetUtcNow();
            if (Tamper && datagram.Length > 0) { datagram[^1] ^= 0x01; }
            Forwarded?.Invoke(datagram);
            await _socket.SendToAsync(datagram, SocketFlags.None, to).ConfigureAwait(false);
            return;
        }
        if (datagram.AsSpan().StartsWith(ReflectAsk) && datagram.Length == 4 + TokenSize)
        {
            var answer = new List<byte>(ReflectAnswer.ToArray());
            answer.AddRange(datagram.AsSpan(4, TokenSize).ToArray());
            answer.AddRange(Encoding.ASCII.GetBytes(from.ToString()));
            await _socket.SendToAsync(answer.ToArray(), SocketFlags.None, from).ConfigureAwait(false);
            return;
        }
        if (datagram.AsSpan().StartsWith(SlotAsk) && datagram.Length == 4 + TokenSize
            && _slots.TryGetValue(Convert.ToHexString(datagram.AsSpan(4, TokenSize)), out var asked) && asked.Join(from))
        {
            _routes[from] = asked;
            asked.Seen = _time.GetUtcNow();
            var answer = new byte[4 + TokenSize];
            SlotAnswer.CopyTo(answer);
            datagram.AsSpan(4, TokenSize).CopyTo(answer.AsSpan(4));
            await _socket.SendToAsync(answer, SocketFlags.None, from).ConfigureAwait(false);
        }
    }

    private void Expire()
    {
        var now = _time.GetUtcNow();
        foreach (var (token, slot) in _slots)
        {
            if (now - slot.Seen < SlotIdle) { continue; }
            _slots.TryRemove(token, out _);
            foreach (var endpoint in slot.Endpoints) { _routes.TryRemove(endpoint, out _); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) { return; }
        await _stopping.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        await Task.WhenAll(_receiving).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private sealed class Slot(string machineA, string machineB, DateTimeOffset created)
    {
        private readonly Lock _gate = new();
        private IPEndPoint? _first;
        private IPEndPoint? _second;

        public string MachineA { get; } = machineA;

        public string MachineB { get; } = machineB;

        public DateTimeOffset Seen { get; set; } = created;

        public IPEndPoint[] Endpoints
        {
            get { lock (_gate) { return [.. new[] { _first, _second }.OfType<IPEndPoint>()]; } }
        }

        // The first two endpoints that present the token; a re-sent registration from either is idempotent.
        public bool Join(IPEndPoint endpoint)
        {
            lock (_gate)
            {
                if (endpoint.Equals(_first) || endpoint.Equals(_second)) { return true; }
                if (_first is null) { _first = endpoint; return true; }
                if (_second is null) { _second = endpoint; return true; }
                return false;
            }
        }

        public IPEndPoint? Other(IPEndPoint endpoint)
        {
            lock (_gate) { return endpoint.Equals(_first) ? _second : endpoint.Equals(_second) ? _first : null; }
        }
    }
}
