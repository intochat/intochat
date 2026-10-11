using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DigitalBrain.Aspire.Hosting;

// Whether a QUIC listener answers on an endpoint, asked without credentials: a long-header packet of a version reserved to
// force version negotiation (RFC 9000 §6.3, 0x?a?a?a?a), padded to the 1200 bytes a server needs before it answers one, makes
// any QUIC listener reply with a version negotiation packet that echoes the connection ids (§17.2.1). A port nobody listens on
// answers with ICMP port unreachable, which the socket reports as a reset (Windows) or a refusal.
internal sealed class QuicListenerHealthCheck(Func<IPEndPoint?> endpoint) : IHealthCheck
{
    // A loopback answer takes microseconds; the bound only keeps a lost datagram from stalling the resource's health loop.
    public static readonly TimeSpan Within = TimeSpan.FromSeconds(2);

    private const int Datagram = 1200;
    private const uint ForcedNegotiation = 0x1a2a3a4a;
    private const int IdLength = 8;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (endpoint() is not { } target) { return HealthCheckResult.Unhealthy("The endpoint is not allocated yet."); }
        var destination = RandomNumberGenerator.GetBytes(IdLength);
        var source = RandomNumberGenerator.GetBytes(IdLength);
        using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(Probe(destination, source), SocketFlags.None, cancellationToken).ConfigureAwait(false);
        var reply = new byte[Datagram];
        int received;
        try { received = await socket.ReceiveAsync(reply, SocketFlags.None, cancellationToken).ConfigureAwait(false); }
        catch (SocketException unreachable) when (unreachable.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
        { return HealthCheckResult.Unhealthy($"No QUIC listener on {target}."); }
        return Negotiates(reply.AsSpan(0, received), destination, source)
            ? HealthCheckResult.Healthy($"A QUIC listener answers on {target}.")
            : HealthCheckResult.Unhealthy($"{target} answered with something other than a QUIC version negotiation.");
    }

    private static byte[] Probe(byte[] destination, byte[] source)
    {
        var packet = new byte[Datagram];
        packet[0] = 0xC0; // long header, fixed bit
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), ForcedNegotiation);
        packet[5] = (byte)destination.Length;
        destination.CopyTo(packet, 6);
        packet[6 + destination.Length] = (byte)source.Length;
        source.CopyTo(packet, 7 + destination.Length);
        return packet;
    }

    // Version 0, addressed back: the probe's source id as its destination, its destination id as its source.
    private static bool Negotiates(ReadOnlySpan<byte> reply, byte[] destination, byte[] source)
        => reply.Length >= 7 + source.Length + destination.Length
            && (reply[0] & 0x80) != 0
            && BinaryPrimitives.ReadUInt32BigEndian(reply[1..]) == 0
            && reply[5] == source.Length && reply.Slice(6, source.Length).SequenceEqual(source)
            && reply[6 + source.Length] == destination.Length && reply.Slice(7 + source.Length, destination.Length).SequenceEqual(destination);
}
