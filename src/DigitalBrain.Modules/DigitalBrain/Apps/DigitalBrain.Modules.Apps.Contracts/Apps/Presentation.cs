using DigitalBrain.Flame.Scene;
using DigitalBrain.Flutter;

namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("apps.sphere")]
public sealed record Sphere([property: Id(0)] string Icon, [property: Id(1)] string[] BlackboardKeys, [property: Id(2)] BehaviorTree? Tree = null);

[GenerateSerializer, Alias("apps.presentation")]
public sealed record Presentation([property: Id(0)] Sphere Sphere, [property: Id(1)] UiChildRef? Side = null, [property: Id(2)] UiChildRef? Main = null);

[GenerateSerializer, Alias("apps.app-presentation")]
public sealed record AppPresentation(
    [property: Id(0)] long ProgramRevision, [property: Id(1)] long Revision,
    [property: Id(2)] Presentation? Root, [property: Id(3)] string? Error,
    [property: Id(4)] Dictionary<string, string> Blackboard);
