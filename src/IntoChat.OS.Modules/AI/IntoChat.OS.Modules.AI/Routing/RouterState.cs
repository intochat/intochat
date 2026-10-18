using IntoChat.OS;
using IntoChat.OS.Framework;

namespace IntoChat.OS.AI.Routing;

// One decision the router made, kept so the next request is judged against what came before it.
[GenerateSerializer, Alias("ai.router.record")]
internal sealed record RouteRecord(
    [property: Id(0)] string Request,
    [property: Id(1)] string Contract,
    [property: Id(2)] string Method);

// The router's own state: the decisions it has made and the requests asked of it, most recent last.
[GenerateSerializer, Alias("ai.router.storage")]
[ExportableState]
internal sealed record RouterStorage
{
    [Id(0)] public RouteRecord[] Recent { get; init; } = [];
    [Id(1)] public PendingRoute[] Asked { get; init; } = [];
}

[GenerateSerializer, Alias("ai.router.decided")]
internal sealed record RouterDecided(string Router, int Count) : Signal;
