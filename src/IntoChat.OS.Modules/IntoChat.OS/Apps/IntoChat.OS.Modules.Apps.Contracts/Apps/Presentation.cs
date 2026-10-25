using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flutter;

namespace IntoChat.OS.Modules.Apps;

[GenerateSerializer, Alias("apps.sphere")]
public sealed record Sphere([property: Id(0)] string Icon, [property: Id(1)] string[] BlackboardKeys, [property: Id(2)] BehaviorTree? Tree = null);

[GenerateSerializer, Alias("apps.presentation")]
public sealed record Presentation([property: Id(0)] Sphere? Sphere = null, [property: Id(1)] UiChildRef? Side = null, [property: Id(2)] UiChildRef? Main = null,
    [property: Id(3)] PresentationView[]? Views = null);

[GenerateSerializer, Alias("apps.presentation-view")]
public sealed record PresentationView([property: Id(0)] string Id, [property: Id(1)] UiChildRef Root);

[GenerateSerializer, Alias("apps.app-presentation")]
public sealed record AppPresentation(
    [property: Id(0)] long ProgramRevision, [property: Id(1)] long Revision,
    [property: Id(2)] Presentation? Root, [property: Id(3)] string? Error,
    [property: Id(4)] Dictionary<string, string> Blackboard);
