using System.Net;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Links;

// The peer's half of a link (R84, R88): when the trust host pushes a rendezvous, this machine reflects its own
// punching socket, answers with that offer, then punches and takes its role on the punched port; when it pushes a
// relay slot, it joins the slot from a fresh port and listens there for the requester.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed partial class RendezvousHandler(QuicMachineTransport transport, ILogger<RendezvousHandler> logger) : IRendezvousHandler
{
    // The nonce is the trust host's: random ‖ its signature over requester, peer and random, made with the key
    // that endorsed the trust link.
    public void Verify(RendezvousOffer offer, byte[] trustPublicKey)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var random = offer.Nonce.AsSpan(0, Math.Min(Links.Rendezvous.NonceRandom, offer.Nonce.Length));
        if (offer.Nonce.Length <= Links.Rendezvous.NonceRandom
            || !MachineKeys.Verify(trustPublicKey, Links.Rendezvous.NonceSigned(offer.PeerMachineId, transport.LocalMachineId, random), offer.Nonce[Links.Rendezvous.NonceRandom..]))
        { throw new UnauthorizedAccessException("The rendezvous nonce is not the trust host's."); }
    }

    public async Task<RendezvousOffer> Handle(RendezvousOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var end = await transport.PeerEnd(offer.PeerMachineId, cancellationToken).ConfigureAwait(false);
        var socket = HolePunch.Open();
        try
        {
            var port = HolePunch.PortOf(socket);
            if (transport.DirectBlocked) { return new RendezvousOffer(transport.LocalMachineId, QuicMachineTransport.Unroutable, [], offer.Nonce); }
            var reflected = await transport.Reflect(socket).ConfigureAwait(false);
            var mine = new RendezvousOffer(transport.LocalMachineId, reflected, HolePunch.Candidates(port), offer.Nonce);
            var punching = socket;
            socket = null;
            _ = Task.Run(() => PunchAndLink(punching, port, offer, end), CancellationToken.None);
            return mine;
        }
        finally { socket?.Dispose(); }
    }

    private async Task PunchAndLink(System.Net.Sockets.Socket socket, int port, RendezvousOffer offer, LinkEnd end)
    {
        try
        {
            IPEndPoint? heard;
            using (socket)
            { heard = await HolePunch.Punch(socket, [offer.PeerReflected, .. offer.PeerCandidates], offer.Nonce, transport.Options.PunchFor, CancellationToken.None).ConfigureAwait(false); }
            if (await transport.Establish(offer.PeerMachineId, end, port, heard ?? offer.PeerReflected, LinkPath.Direct, CancellationToken.None).ConfigureAwait(false) is { } link)
            { transport.Keep(link); }
        }
        catch (Exception failed) { Log.NoDirectLink(logger, failed, offer.PeerMachineId); }
    }

    public async Task Join(RelayOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var end = await transport.PeerEnd(offer.PeerMachineId, cancellationToken).ConfigureAwait(false);
        var slot = new RelaySlot(await transport.RelayEndPoint().ConfigureAwait(false), offer.Token);
        int port;
        using (var socket = HolePunch.Open())
        {
            port = HolePunch.PortOf(socket);
            await HolePunch.Join(socket, slot, cancellationToken).ConfigureAwait(false);
        }
        // Listening before answering: the requester dials as soon as this push returns.
        var accepted = new TaskCompletionSource<QuicMachineLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = await QuicLinkListener.Start(new IPEndPoint(IPAddress.Any, port), end, link => accepted.TrySetResult(link), cancellationToken, LinkPath.Relayed).ConfigureAwait(false);
        _ = Task.Run(async () =>
        {
            try { transport.Keep(await accepted.Task.WaitAsync(transport.Options.IdleTimeout).ConfigureAwait(false)); }
            catch (TimeoutException failed) { Log.NoDirectLink(logger, failed, offer.PeerMachineId); }
            finally { await listener.DisposeAsync().ConfigureAwait(false); }
        }, CancellationToken.None);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "No link with machine {MachineId} came of a rendezvous.")]
        public static partial void NoDirectLink(ILogger logger, Exception failed, string machineId);
    }
}
