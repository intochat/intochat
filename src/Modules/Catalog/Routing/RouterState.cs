using DigitalBrain;
using DigitalBrain.State;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Apps.Routing;

// One decision the router made, kept so the next request is judged against what came before it.
[GenerateSerializer, Alias("ai.router.record")]
internal sealed record RouteRecord(
    [property: Id(0)] string Request,
    [property: Id(1)] string Contract,
    [property: Id(2)] string Method);

// The router's own state: the decisions it has made, most recent last.
[GenerateSerializer, Alias("ai.router.storage")]
[ExportableState]
internal sealed record RouterStorage
{
    [Id(0)] public RouteRecord[] Recent { get; init; } = [];
    // Id(1) retired: Asked is no longer written; kept so the persisted identity is unchanged.
    [Id(1)] public PendingRoute[] Asked { get; init; } = [];
}

[Signal("ai.router.decided")]
internal sealed record RouterDecided(string Router, int Count) : DigitalBrain.Signal;
