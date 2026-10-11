namespace DigitalBrain.AI.Routing;

// One request posted through IRouter.Ask. The poster picks the id; the answer arrives as Routed with
// the same id and stays readable through IRouter.Pending.
[GenerateSerializer, Alias("ai.route-request")]
public sealed record RouteRequest(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string State,
    [property: Id(2)] string[]? Allowed = null);

// The router's record of one asked request: Routing until it is decided, then Routed with its
// result (no result, and the reason in Error, when nothing fits) or Failed with the error.
[GenerateSerializer, Alias("ai.pending-route")]
public sealed record PendingRoute(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string State,
    [property: Id(2)] string Status,
    [property: Id(3)] string[]? Allowed = null,
    [property: Id(4)] RouteResult? Result = null,
    [property: Id(5)] string? Error = null);
