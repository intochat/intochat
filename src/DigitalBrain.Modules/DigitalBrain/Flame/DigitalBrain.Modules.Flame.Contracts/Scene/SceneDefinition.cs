using DigitalBrain;

namespace DigitalBrain.Flame.Scene;

[GenerateSerializer, Alias("flame.transform")]
public sealed record Transform([property: Id(0)] double X = 0, [property: Id(1)] double Y = 0, [property: Id(2)] double Z = 0,
    [property: Id(3)] double Scale = 1, [property: Id(4)] double Rotation = 0, [property: Id(5)] double Opacity = 1);

[GenerateSerializer, Alias("flame.tween")]
public sealed record Tween([property: Id(0)] string Property, [property: Id(1)] double From, [property: Id(2)] double To,
    [property: Id(3)] int Milliseconds, [property: Id(4)] string Curve = "easeInOut", [property: Id(5)] bool Repeat = false);

[GenerateSerializer, Alias("flame.node")]
public sealed record SceneNode([property: Id(0)] string Id, [property: Id(1)] string Type, [property: Id(2)] Transform Transform,
    [property: Id(3)] string? Style, [property: Id(4)] Dictionary<string, string> Parameters, [property: Id(5)] SceneNode[] Children,
    [property: Id(6)] Tween[] Tweens, [property: Id(7)] string[] Inputs, [property: Id(8)] BehaviorTree? Tree = null);

[GenerateSerializer, Alias("flame.camera")]
public sealed record SceneCamera([property: Id(0)] double X = 0, [property: Id(1)] double Y = 0, [property: Id(2)] double Zoom = 1);

[GenerateSerializer, Alias("flame.scene-definition")]
public sealed record SceneDefinition([property: Id(0)] SceneNode[] Nodes, [property: Id(1)] SceneCamera Camera,
    [property: Id(2)] string Background = "#0B0F14");

[GenerateSerializer, Alias("flame.scene-state")]
public sealed class SceneState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public SceneDefinition Definition { get; set; } = new([], new());
}
