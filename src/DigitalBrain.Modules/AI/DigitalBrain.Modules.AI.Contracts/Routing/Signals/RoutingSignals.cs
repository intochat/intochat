using DigitalBrain;

namespace DigitalBrain.AI.Routing.Signals;

// The router's answer to one asked request: which contract and method, and how sure it was. Id is
// the RouteRequest's; Contract is the full type name. Error is set when no route could be decided.
[GenerateSerializer, Alias("ai.routed")]
public sealed record Routed(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string Contract,
    [property: Id(2)] string Method,
    [property: Id(3)] double Confidence,
    [property: Id(4)] string? Error = null) : Signal;
