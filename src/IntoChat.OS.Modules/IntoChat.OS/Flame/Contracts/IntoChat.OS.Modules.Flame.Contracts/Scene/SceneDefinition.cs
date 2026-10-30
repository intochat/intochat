using DigitalBrain.State;
using IntoChat.OS;

namespace IntoChat.OS.Modules.Flame.Scene;

public sealed record Transform(double X = 0, double Y = 0, double Z = 0,
    double Scale = 1, double Rotation = 0, double Opacity = 1);

public sealed record Tween(string Property, double From, double To,
    int Milliseconds, string Curve = "easeInOut", bool Repeat = false);

public sealed record SceneNode(string Id, string Type, Transform Transform,
    string? Style, Dictionary<string, string> Parameters, SceneNode[] Children,
    Tween[] Tweens, string[] Inputs, BehaviorTree? Tree = null);

public sealed record SceneCamera(double X = 0, double Y = 0, double Zoom = 1);

public sealed record SceneDefinition(SceneNode[] Nodes, SceneCamera Camera,
    string Background = "#0B0F14");

[ExportableState]
public sealed class SceneState
{
    public string Name { get; set; } = "";
    public long Revision { get; set; }
    public SceneDefinition Definition { get; set; } = new([], new());
}
