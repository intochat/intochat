namespace DigitalBrain.Framework.Links;

public interface IMachineLink : IAsyncDisposable
{
    // Authenticated by the handshake: the id derived from the key that endorsed the peer's link certificate.
    string RemoteMachineId { get; }

    LinkPath Path { get; }

    // One call is one stream: a request frame out, a reply frame back. A failure after the request was sent is
    // never retried here (T20-12).
    Task<LinkReply> Send(LinkRequest request, CancellationToken cancellationToken);
}

public enum LinkPath { Direct, Relayed }
