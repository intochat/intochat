using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Links;

// What a link end answers on a request its peer sent.
internal delegate Task<LinkReply> LinkHandler(QuicMachineLink link, LinkRequest request, CancellationToken cancellationToken);

// One side of a link: its endorsed certificate (read per handshake, so a renewed one is used), the deployment it accepts endorsements for (null: any — only the
// trust link, whose host is pinned), which proved peers it admits, and what it answers.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed record LinkEnd(
    Func<X509Certificate2> Certificate,
    string? DeploymentId,
    Func<LinkPeer, bool> Admits,
    LinkOptions Options,
    Serializer Serializer,
    LinkHandler Handler,
    TimeProvider Time)
{
    public static readonly List<SslApplicationProtocol> Alpn = [new("digitalbrain-link/1")];

    public LinkPeer? Check(X509Certificate? presented)
        => LinkCertificates.Read(presented, DeploymentId, Time) is { } peer && Admits(peer) ? peer : null;
}

// T20-1: one QUIC connection per machine pair, one bidirectional stream per call carrying one request frame and one
// reply frame (the Orleans serializer's bytes). Either end may call; the trust host pushes rendezvous this way.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class QuicMachineLink : IMachineLink
{
    public const string Hello = "link.hello";
    public const long CloseCode = 0x10;
    public const long StreamCode = 0x11;
    private const int MaxFrame = 64 * 1024 * 1024;

    private readonly QuicConnection _connection;
    private readonly LinkEnd _end;
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _serving;

    private QuicMachineLink(QuicConnection connection, LinkPeer peer, LinkEnd end, LinkPath path)
    {
        (_connection, Peer, _end, Path) = (connection, peer, end, path);
        _serving = Task.Run(Serve);
    }

    public LinkPeer Peer { get; }

    public string RemoteMachineId => Peer.MachineId;

    public LinkPath Path { get; }

    public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    // Completes when the connection ends, whoever ended it.
    public Task Closed => _serving;

    public bool IsOpen => !_serving.IsCompleted;

    internal static QuicMachineLink Accepted(QuicConnection connection, LinkPeer peer, LinkEnd end, LinkPath path) => new(connection, peer, end, path);

    // The handshake proves both ends; the hello round trip makes a refusal of this end's certificate surface here
    // (TLS 1.3 completes on the client before the server has judged the client's certificate).
    public static async Task<QuicMachineLink> Dial(IPEndPoint remote, IPEndPoint? local, string? expectedMachineId, LinkEnd end, LinkPath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(end);
        LinkPeer? proved = null;
        var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = remote,
            LocalEndPoint = local,
            DefaultStreamErrorCode = StreamCode,
            DefaultCloseErrorCode = CloseCode,
            IdleTimeout = end.Options.IdleTimeout,
            KeepAliveInterval = end.Options.KeepAlive,
            MaxInboundBidirectionalStreams = 128,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = LinkEnd.Alpn,
                TargetHost = "digitalbrain-link",
                ClientCertificates = [end.Certificate()],
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    (proved = end.Check(certificate)) is { } peer && (expectedMachineId is null || peer.MachineId == expectedMachineId),
            },
        }, cancellationToken).ConfigureAwait(false);
        var link = new QuicMachineLink(connection, proved!, end, path);
        try { await link.Send(new LinkRequest(Hello, [], null, null, Guid.NewGuid()), cancellationToken).ConfigureAwait(false); }
        catch
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return link;
    }

    public async Task<LinkReply> Send(LinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await using var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(_end.Serializer.SerializeToArray(request), completeWrites: true, cancellationToken).ConfigureAwait(false);
            return _end.Serializer.Deserialize<LinkReply>(await ReadAll(stream, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException("A link reply frame is empty.");
        }
        catch (QuicException failed)
        { throw new LinkFailedException($"The link to machine {RemoteMachineId} failed: {failed.Message}", failed); }
        catch (ObjectDisposedException closed)
        { throw new LinkFailedException($"The link to machine {RemoteMachineId} is closed.", closed); }
    }

    private async Task Serve()
    {
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                var stream = await _connection.AcceptInboundStreamAsync(_closing.Token).ConfigureAwait(false);
                _ = Task.Run(() => Answer(stream));
            }
        }
        catch (Exception ended) when (ended is QuicException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task Answer(QuicStream stream)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var request = _end.Serializer.Deserialize<LinkRequest>(await ReadAll(stream, _closing.Token).ConfigureAwait(false))
                    ?? throw new InvalidDataException("A link request frame is empty.");
                LinkReply reply;
                try
                {
                    reply = request.Operation == Hello ? new LinkReply([], null, null)
                        : await _end.Handler(this, request, _closing.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !_closing.IsCancellationRequested)
                { reply = LinkReplies.Failure(error); }
                await stream.WriteAsync(_end.Serializer.SerializeToArray(reply), completeWrites: true, _closing.Token).ConfigureAwait(false);
            }
            catch (Exception ended) when (ended is QuicException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
        }
    }

    private static async Task<byte[]> ReadAll(QuicStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxFrame) { throw new InvalidDataException("A link frame exceeds 64 MiB."); }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_closing.IsCancellationRequested) { return; }
        await _closing.CancelAsync().ConfigureAwait(false);
        try { await _connection.CloseAsync(CloseCode).ConfigureAwait(false); }
        catch (Exception closed) when (closed is QuicException or ObjectDisposedException) { }
        await _connection.DisposeAsync().ConfigureAwait(false);
        await _serving.ConfigureAwait(false);
        _closing.Dispose();
    }
}

// A handler's exception crosses as its type name and message; the caller rethrows the kinds the trust and part
// contracts promise (refusal, unavailability, bad input) and anything else as an InvalidOperationException.
internal static class LinkReplies
{
    public static LinkReply Failure(Exception error) => new(null, error.GetType().FullName, error.Message);

    public static byte[] Payload(LinkReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.ErrorType is not { } type) { return reply.Payload ?? []; }
        var message = reply.ErrorMessage ?? type;
        throw type switch
        {
            "System.UnauthorizedAccessException" => new UnauthorizedAccessException(message),
            "System.ArgumentException" or "System.ArgumentNullException" or "System.ArgumentOutOfRangeException" => new ArgumentException(message),
            "System.IO.InvalidDataException" => new InvalidDataException(message),
            "System.Collections.Generic.KeyNotFoundException" => new KeyNotFoundException(message),
            _ when type == typeof(ServiceUnavailableException).FullName => new ServiceUnavailableException(message),
            _ => new InvalidOperationException($"{type}: {message}"),
        };
    }
}
