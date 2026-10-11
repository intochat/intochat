using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Links;

// This machine's links (T20-1, T20-6). The trust link is dialed first (pinned, T20-3) and carries the trust clients
// and the rendezvous. Connect(peer): reuse a live link; else rendezvous through the trust host, punch, and let QUIC
// take the punched port over (the lower machine id listens, the other dials); no handshake within DirectWithin →
// a trust relay slot. Every machine link is mutually authenticated against the peer's registration (T20-2).
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed partial class QuicMachineTransport(IOptions<TrustOptions> trust, IOptions<LinkOptions> links, MachineIdentity machine, IMachineSigningKey key,
    TrustPin pin, Serializer serializer, TimeProvider time, ILogger<QuicMachineTransport> logger) : IMachineTransport, IRelayClient, IAsyncDisposable
{
    // Where an unreachable machine says it is (TEST-NET-1, RFC 5737): nothing answers there.
    internal static readonly IPEndPoint Unroutable = new(IPAddress.Parse("192.0.2.1"), 9);

    private readonly ConcurrentDictionary<string, QuicMachineLink> _links = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connecting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LinkHandler> _operations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _trustGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private QuicMachineLink? _trust;
    private IPEndPoint? _trustEndPoint;
    private LinkCertificate? _trustCertificate;
    private LinkCertificate? _certificate;
    private string? _deployment;
    private int? _relayPort;

    public string LocalMachineId => machine.MachineId;

    internal LinkOptions Options => links.Value;

    // Facts only: this machine cannot be reached directly (its offers name an unroutable endpoint and it never
    // punches), so every link it takes part in goes through the relay.
    internal bool DirectBlocked { get; set; }

    internal RendezvousHandler? RendezvousHandler { get; set; }

    // What this machine answers when a peer calls it over a machine link (Task 22: grain-call).
    public void Handle(string operation, LinkHandler handler) => _operations[operation] = handler;

    public async Task<IMachineLink> Connect(string machineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        if (machineId == LocalMachineId) { throw new ArgumentException("A machine does not link to itself.", nameof(machineId)); }
        if (Live(machineId) is { } live) { return live; }
        var gate = _connecting.GetOrAdd(machineId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Live(machineId) is { } raced) { return raced; }
            var end = await PeerEnd(machineId, cancellationToken).ConfigureAwait(false);
            var link = (DirectBlocked ? null : await Direct(machineId, end, cancellationToken).ConfigureAwait(false))
                ?? await Relayed(machineId, end, cancellationToken).ConfigureAwait(false);
            Keep(link);
            return link;
        }
        finally { gate.Release(); }
    }

    private QuicMachineLink? Live(string machineId) => _links.TryGetValue(machineId, out var link) && link.IsOpen ? link : null;

    internal void Keep(QuicMachineLink link)
    {
        _links[link.RemoteMachineId] = link;
        Log.Linked(logger, link.RemoteMachineId, link.Path, link.RemoteEndPoint);
        _ = link.Closed.ContinueWith(_ => _links.TryRemove(new(link.RemoteMachineId, link)), TaskScheduler.Default);
    }

    // The end a link with `peer` uses: this deployment's certificate, admitting exactly the peer's registered key.
    internal async Task<LinkEnd> PeerEnd(string peer, CancellationToken cancellationToken)
    {
        var registered = await Call<string, RegisteredMachine?>(TrustOperations.Machine, peer, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException($"Machine {peer} is not registered with the trust deployment; no link is made to it.");
        _deployment ??= await Call<bool, string>(TrustOperations.Deployment, true, cancellationToken).ConfigureAwait(false);
        _certificate ??= new LinkCertificate(key, _deployment, time);
        var certificate = _certificate;
        return new LinkEnd(() => certificate.Current, _deployment,
            proved => proved.MachineId == peer && proved.PublicKey.AsSpan().SequenceEqual(registered.PublicKey),
            links.Value, serializer, Answer, time);
    }

    private async Task<QuicMachineLink?> Direct(string peer, LinkEnd end, CancellationToken cancellationToken)
    {
        var socket = HolePunch.Open();
        IPEndPoint? heard;
        RendezvousOffer? offer;
        int port;
        try
        {
            port = HolePunch.PortOf(socket);
            var reflected = await Reflect(socket).ConfigureAwait(false);
            offer = await Rendezvous(peer, reflected, HolePunch.Candidates(port)).ConfigureAwait(false);
            if (offer is null) { return null; }
            heard = await HolePunch.Punch(socket, [offer.PeerReflected, .. offer.PeerCandidates], offer.Nonce, links.Value.PunchFor, cancellationToken).ConfigureAwait(false);
        }
        finally { socket.Dispose(); }
        return await Establish(peer, end, port, heard ?? offer.PeerReflected, LinkPath.Direct, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QuicMachineLink> Relayed(string peer, LinkEnd end, CancellationToken cancellationToken)
    {
        var slot = await Allocate(peer).ConfigureAwait(false);
        int port;
        using (var socket = HolePunch.Open())
        {
            port = HolePunch.PortOf(socket);
            await HolePunch.Join(socket, slot, cancellationToken).ConfigureAwait(false);
        }
        // The peer joined first and listens on its relayed port; the requester always dials a relay slot.
        return await Dial(peer, end, port, slot.Endpoint, LinkPath.Relayed, links.Value.IdleTimeout, cancellationToken).ConfigureAwait(false)
            ?? throw new LinkFailedException($"No link to machine {peer}, directly or through the relay.");
    }

    // R84: the lower machine id listens on its punched port, the other dials from its punched port.
    internal Task<QuicMachineLink?> Establish(string peer, LinkEnd end, int port, IPEndPoint remote, LinkPath path, CancellationToken cancellationToken)
        => string.CompareOrdinal(LocalMachineId, peer) < 0
            ? Listen(end, port, path, links.Value.DirectWithin, cancellationToken)
            : Dial(peer, end, port, remote, path, links.Value.DirectWithin, cancellationToken);

    internal static async Task<QuicMachineLink?> Listen(LinkEnd end, int port, LinkPath path, TimeSpan within, CancellationToken cancellationToken)
    {
        var accepted = new TaskCompletionSource<QuicMachineLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = await QuicLinkListener.Start(new IPEndPoint(IPAddress.Any, port), end, link => accepted.TrySetResult(link), cancellationToken, path).ConfigureAwait(false);
        try { return await accepted.Task.WaitAsync(within, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    private static async Task<QuicMachineLink?> Dial(string peer, LinkEnd end, int port, IPEndPoint remote, LinkPath path, TimeSpan within, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(within);
        while (!deadline.IsCancellationRequested)
        {
            try { return await QuicMachineLink.Dial(remote, new IPEndPoint(IPAddress.Any, port), peer, end, path, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            // The listener may not be up yet (it re-binds its punched port): try again until the deadline.
            catch (Exception failed) when (failed is System.Net.Quic.QuicException or LinkFailedException or SocketException)
            {
                try { await Task.Delay(50, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            }
        }
        return null;
    }

    // Requests a peer sends this machine; the trust host's pushes are honoured only from the trust link.
    private async Task<LinkReply> Answer(QuicMachineLink link, LinkRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation is Links.Rendezvous.Operation or Links.Rendezvous.RelayOperation)
        {
            if (!ReferenceEquals(link, _trust)) { throw new UnauthorizedAccessException($"Only the trust host pushes {request.Operation}."); }
            var handler = RendezvousHandler ?? throw new InvalidOperationException("This machine takes no rendezvous.");
            if (request.Operation == Links.Rendezvous.Operation)
            {
                var offer = serializer.Deserialize<RendezvousOffer>(request.Payload) ?? throw new InvalidDataException("An empty rendezvous offer.");
                handler.Verify(offer, link.Peer.PublicKey);
                return new LinkReply(serializer.SerializeToArray(await handler.Handle(offer, cancellationToken).ConfigureAwait(false)), null, null);
            }
            await handler.Join(serializer.Deserialize<RelayOffer>(request.Payload) ?? throw new InvalidDataException("An empty relay offer."), cancellationToken).ConfigureAwait(false);
            return new LinkReply([], null, null);
        }
        if (_operations.TryGetValue(request.Operation, out var operation)) { return await operation(link, request, cancellationToken).ConfigureAwait(false); }
        throw new InvalidOperationException($"Machine {LocalMachineId} answers no '{request.Operation}'.");
    }

    // The trust link: dialed on first use and again whenever it has closed (a trust restart), pinned every time.
    internal async Task<QuicMachineLink> TrustLink(CancellationToken cancellationToken)
    {
        if (_trust is { IsOpen: true } open) { return open; }
        await _trustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_trust is { IsOpen: true } raced) { return raced; }
            _trustEndPoint ??= await Resolve(trust.Value.Endpoint ?? throw new InvalidOperationException($"{TrustOptions.Key}:Endpoint is not set."), cancellationToken).ConfigureAwait(false);
            // The trust link's certificate is made before the deployment is known; the trust host accepts any (R90).
            _trustCertificate ??= new LinkCertificate(key, "", time);
            var trustCertificate = _trustCertificate;
            var end = new LinkEnd(() => trustCertificate.Current, null, _ => true, links.Value, serializer, Answer, time);
            var local = links.Value.Port is > 0 and var port ? new IPEndPoint(IPAddress.Any, port) : null;
            var link = await QuicMachineLink.Dial(_trustEndPoint, local, null, end, LinkPath.Direct, cancellationToken).ConfigureAwait(false);
            try { pin.Check(link.RemoteMachineId); }
            catch
            {
                await link.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            Log.TrustLinked(logger, link.RemoteMachineId, _trustEndPoint);
            return _trust = link;
        }
        finally { _trustGate.Release(); }
    }

    // One trust call: never retried once sent (T20-12); a dead link is re-dialed by the next call.
    internal async Task<byte[]> CallTrust(LinkRequest request, CancellationToken cancellationToken)
    {
        var link = await TrustLink(cancellationToken).ConfigureAwait(false);
        return LinkReplies.Payload(await link.Send(request, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TResult> Call<TArgs, TResult>(string operation, TArgs args, CancellationToken cancellationToken)
        => serializer.Deserialize<TResult>(await CallTrust(new LinkRequest(operation, serializer.SerializeToArray(args), null, null, Guid.NewGuid()), cancellationToken).ConfigureAwait(false))!;

    private static async Task<IPEndPoint> Resolve(string hostAndPort, CancellationToken cancellationToken)
    {
        if (!TrustOptions.TryParse(hostAndPort, out var host, out var port)) { throw new InvalidOperationException($"'{hostAndPort}' is not host:port."); }
        if (IPAddress.TryParse(host, out var address)) { return new IPEndPoint(address, port); }
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return new IPEndPoint(addresses.First(candidate => candidate.AddressFamily == AddressFamily.InterNetwork), port);
    }

    internal async Task<IPEndPoint> RelayEndPoint()
    {
        await TrustLink(_stopping.Token).ConfigureAwait(false);
        _relayPort ??= await Call<bool, int>(TrustOperations.RelayPort, true, _stopping.Token).ConfigureAwait(false);
        var trustAddress = _trustEndPoint!.Address;
        return new IPEndPoint(trustAddress.Equals(IPAddress.Any) ? IPAddress.Loopback : trustAddress, _relayPort.Value);
    }

    public async Task<IPEndPoint> Reflect(Socket punching)
        => await HolePunch.Reflect(punching, await RelayEndPoint().ConfigureAwait(false), _stopping.Token).ConfigureAwait(false);

    public async Task<RelaySlot> Allocate(string peerMachineId)
        => new(await RelayEndPoint().ConfigureAwait(false), await Call<string, byte[]>(TrustOperations.RelayAllocate, peerMachineId, _stopping.Token).ConfigureAwait(false));

    public Task<RendezvousOffer?> Rendezvous(string peerMachineId, IPEndPoint reflected, IPEndPoint[] candidates)
        => Call<(string, IPEndPoint, IPEndPoint[]), RendezvousOffer?>(TrustOperations.RelayRendezvous, (peerMachineId, reflected, candidates), _stopping.Token);

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) { return; }
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var link in _links.Values) { await link.DisposeAsync().ConfigureAwait(false); }
        if (_trust is { } trustLink) { await trustLink.DisposeAsync().ConfigureAwait(false); }
        _certificate?.Dispose();
        _trustCertificate?.Dispose();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Linked to machine {MachineId} ({Path}) at {Endpoint}.")]
        public static partial void Linked(ILogger logger, string machineId, LinkPath path, IPEndPoint endpoint);

        [LoggerMessage(Level = LogLevel.Information, Message = "Linked to trust host {TrustId} at {Endpoint}.")]
        public static partial void TrustLinked(ILogger logger, string trustId, IPEndPoint endpoint);
    }
}
