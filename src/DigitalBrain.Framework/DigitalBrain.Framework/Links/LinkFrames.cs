namespace DigitalBrain.Framework.Links;

[GenerateSerializer, Alias("framework.link.request")]
public sealed record LinkRequest(
    [property: Id(0)] string Operation,
    [property: Id(1)] byte[] Payload,
    [property: Id(2)] string? TraceParent,
    [property: Id(3)] string? TraceState,
    [property: Id(4)] Guid CallId);

[GenerateSerializer, Alias("framework.link.reply")]
public sealed record LinkReply(
    [property: Id(0)] byte[]? Payload,
    [property: Id(1)] string? ErrorType,
    [property: Id(2)] string? ErrorMessage);

// The link itself failed (closed, idle, tampered with): whether the peer acted on the request is unknown.
public sealed class LinkFailedException(string message, Exception? inner = null) : Exception(message, inner);
