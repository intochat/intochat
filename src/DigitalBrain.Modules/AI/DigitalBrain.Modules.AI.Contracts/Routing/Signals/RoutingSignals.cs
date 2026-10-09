using DigitalBrain;

namespace DigitalBrain.AI.Routing.Signals;

// The router's decision for one request:which contract and method, and how sure it was.
// Published by Route (and so by Dispatch, which routes first). Contract is the full type name.

[GenerateSerializer, Alias("ai.routed")]
public sealed record Routed(
    [property: Id(0)] string State,
    [property: Id(1)] string Contract,
    [property: Id(2)] string Method,
    [property: Id(3)] double Confidence) : Signal;