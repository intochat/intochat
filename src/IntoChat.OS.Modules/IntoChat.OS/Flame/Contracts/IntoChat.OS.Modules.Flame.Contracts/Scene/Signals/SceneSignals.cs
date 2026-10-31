using DigitalBrain;
using IntoChat.OS;

namespace IntoChat.OS.Modules.Flame.Scene.Signals;

[Signal("flame.scene-changed")]
public sealed record SceneChanged(string Name, long Revision) : DigitalBrain.Signal;

[Signal("flame.scene-input")]
public sealed record SceneInput(string Name, string NodeId, string Kind,
    double X, double Y, string? Payload) : DigitalBrain.Signal;
