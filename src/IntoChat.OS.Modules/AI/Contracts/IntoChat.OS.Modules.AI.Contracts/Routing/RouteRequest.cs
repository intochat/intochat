namespace IntoChat.OS.Modules.AI.Routing;

// Retired: only kept so persisted RouterStorage.Asked still deserializes.
// The router's record of one asked request: Routing until it is decided, then Routed with its
// result (no result, and the reason in Error, when nothing fits) or Failed with the error.
public sealed record PendingRoute(
    Guid Id,
    string State,
    string Status,
    string[]? Allowed = null,
    RouteResult? Result = null,
    string? Error = null);
