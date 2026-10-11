using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Links;

// R88: a machine's offer is its punching socket's NAT-reflected endpoint (no chosen port) plus its candidates; the
// nonce is minted and signed by the trust host and echoed in the first punch packet. Rendezvous returns the peer's
// offer to the dialer, carrying the same nonce.
[GenerateSerializer, Alias("platform.links.rendezvous-offer")]
internal sealed record RendezvousOffer(
    [property: Id(0)] string PeerMachineId,
    [property: Id(1)] IPEndPoint PeerReflected,
    [property: Id(2)] IPEndPoint[] PeerCandidates,
    [property: Id(3)] byte[] Nonce);

// The relay fallback (T20-6): the peer joins the slot with this token and listens on its relayed port.
[GenerateSerializer, Alias("platform.links.relay-offer")]
internal sealed record RelayOffer([property: Id(0)] string PeerMachineId, [property: Id(1)] byte[] Token);

// A machine's view of the trust host's relay port and rendezvous service, over its trust link.
internal interface IRelayClient
{
    // The punching socket's endpoint as the relay sees it (R88), asked per rendezvous so the mapping is that socket's.
    Task<IPEndPoint> Reflect(Socket punching);

    // A slot for this machine and the peer; when it returns the peer has joined the slot and listens on it.
    Task<RelaySlot> Allocate(string peerMachineId);

    // Pushes this machine's offer to the peer over the peer's trust link (R84); the peer's offer, or null when the
    // peer holds no trust link.
    Task<RendezvousOffer?> Rendezvous(string peerMachineId, IPEndPoint reflected, IPEndPoint[] candidates);
}

// Machine side, on the transport: what a machine does when the trust host pushes a rendezvous or a relay slot.
internal interface IRendezvousHandler
{
    // Reflects its own punching socket and answers with its offer (R88), then punches; the lower machine id listens.
    Task<RendezvousOffer> Handle(RendezvousOffer offer, CancellationToken cancellationToken);

    // Joins the relay slot from a fresh port and listens there for the dialer.
    Task Join(RelayOffer offer, CancellationToken cancellationToken);
}

// The trust host's side of a rendezvous: it reaches the peer over the peer's own trust link and relays its answer.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class Rendezvous(Func<string, QuicMachineLink?> trustLinkOf, Relay relay, IMachineSigningKey trustKey, Serializer serializer)
{
    public const string Operation = "rendezvous";
    public const string RelayOperation = "relay";
    public const int NonceRandom = 16;

    public static byte[] NonceSigned(string requester, string peer, ReadOnlySpan<byte> random)
        => [.. Encoding.UTF8.GetBytes($"digitalbrain-rendezvous|{requester}|{peer}|"), .. random];

    public async Task<RendezvousOffer?> Offer(string requester, string peer, IPEndPoint reflected, IPEndPoint[] candidates, CancellationToken cancellationToken)
    {
        if (trustLinkOf(peer) is not { IsOpen: true } link) { return null; }
        var random = RandomNumberGenerator.GetBytes(NonceRandom);
        byte[] nonce = [.. random, .. trustKey.Sign(NonceSigned(requester, peer, random))];
        var reply = await link.Send(new LinkRequest(Operation, serializer.SerializeToArray(new RendezvousOffer(requester, reflected, candidates, nonce)),
            null, null, Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        var answer = serializer.Deserialize<RendezvousOffer>(LinkReplies.Payload(reply));
        if (answer is null || answer.PeerMachineId != peer || !answer.Nonce.AsSpan().SequenceEqual(nonce))
        { throw new InvalidDataException($"Machine {peer} answered the rendezvous with another offer."); }
        return answer;
    }

    public async Task<byte[]> Slot(string requester, string peer, CancellationToken cancellationToken)
    {
        if (trustLinkOf(peer) is not { IsOpen: true } link)
        { throw new ServiceUnavailableException($"Machine {peer} holds no trust link; it cannot join a relay slot."); }
        var token = relay.Allocate(requester, peer);
        LinkReplies.Payload(await link.Send(new LinkRequest(RelayOperation, serializer.SerializeToArray(new RelayOffer(requester, token)),
            null, null, Guid.NewGuid()), cancellationToken).ConfigureAwait(false));
        return token;
    }
}
