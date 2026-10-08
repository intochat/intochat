namespace DigitalBrain.AI.Routing;

// The chosen contract (full type name) and method, with the router's confidence in that choice.
[GenerateSerializer, Alias("ai.route-result")]
public sealed record RouteResult(
    [property: Id(0)] string Contract,
    [property: Id(1)] string Method,
    [property: Id(2)] double Confidence,
    [property: Id(3)] Dictionary<string, double> Probabilities);

// The route, whether it was dispatched, and the call it made: the neuron key, the JSON arguments,
// and the JSON result (or the refusal reason).
[GenerateSerializer, Alias("ai.route-outcome")]
public sealed record RouteOutcome(
    [property: Id(0)] RouteResult Route,
    [property: Id(1)] bool Dispatched,
    [property: Id(2)] string? Key = null,
    [property: Id(3)] string? Arguments = null,
    [property: Id(4)] string? Result = null,
    [property: Id(5)] string? Error = null);
