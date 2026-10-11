using System.Net;
using System.Net.Quic;
using System.Net.Security;
using DigitalBrain.Framework.Links;

namespace DigitalBrain.Platform.Links;

// Accepts links on one UDP port: a peer is admitted at the handshake when its endorsement holds and the end admits
// it; every accepted link is handed to `accepted`. A failed handshake never stops the loop.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class QuicLinkListener : IAsyncDisposable
{
    private readonly QuicListener _listener;
    private readonly LinkEnd _end;
    private readonly Action<QuicMachineLink> _accepted;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private readonly List<QuicMachineLink> _links = [];

    private QuicLinkListener(QuicListener listener, LinkEnd end, Action<QuicMachineLink> accepted, LinkPath path)
    {
        (_listener, _end, _accepted, Path) = (listener, end, accepted, path);
        _accepting = Task.Run(Accept);
    }

    public IPEndPoint LocalEndPoint => _listener.LocalEndPoint;

    public LinkPath Path { get; }

    public static async Task<QuicLinkListener> Start(IPEndPoint at, LinkEnd end, Action<QuicMachineLink> accepted, CancellationToken cancellationToken,
        LinkPath path = LinkPath.Direct)
    {
        ArgumentNullException.ThrowIfNull(at);
        ArgumentNullException.ThrowIfNull(end);
        // Built per connection, so every handshake presents the end's current (renewed) certificate.
        QuicServerConnectionOptions Options() => new()
        {
            DefaultStreamErrorCode = QuicMachineLink.StreamCode,
            DefaultCloseErrorCode = QuicMachineLink.CloseCode,
            IdleTimeout = end.Options.IdleTimeout,
            KeepAliveInterval = end.Options.KeepAlive,
            MaxInboundBidirectionalStreams = 128,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = LinkEnd.Alpn,
                ServerCertificate = end.Certificate(),
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => end.Check(certificate) is not null,
            },
        };
        var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = at,
            ApplicationProtocols = LinkEnd.Alpn,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(Options()),
        }, cancellationToken).ConfigureAwait(false);
        return new QuicLinkListener(listener, end, accepted, path);
    }

    private async Task Accept()
    {
        while (!_stopping.IsCancellationRequested)
        {
            QuicConnection connection;
            try { connection = await _listener.AcceptConnectionAsync(_stopping.Token).ConfigureAwait(false); }
            catch (Exception stopped) when (stopped is OperationCanceledException or ObjectDisposedException) { return; }
            catch (Exception) { continue; }
            if (_end.Check(connection.RemoteCertificate) is not { } peer)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            var link = QuicMachineLink.Accepted(connection, peer, _end, Path);
            lock (_links) { _links.Add(link); }
            _accepted(link);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) { return; }
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
        await _accepting.ConfigureAwait(false);
        _stopping.Dispose();
    }

    // Links this listener accepted stay open after it stops (a punched link outlives its rendezvous listener);
    // the trust endpoint closes its own at shutdown.
    public async ValueTask CloseLinks()
    {
        QuicMachineLink[] links;
        lock (_links) { links = [.. _links]; }
        foreach (var link in links) { await link.DisposeAsync().ConfigureAwait(false); }
    }
}
