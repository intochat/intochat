using IntoChat.OS;

namespace IntoChat.OS.Flame.Scene.Signals;

[GenerateSerializer, Alias("flame.scene-changed")]
public sealed record SceneChanged([property: Id(0)] string Name, [property: Id(1)] long Revision) : Signal;

[GenerateSerializer, Alias("flame.scene-input")]
public sealed record SceneInput([property: Id(0)] string Name, [property: Id(1)] string NodeId, [property: Id(2)] string Kind,
    [property: Id(3)] double X, [property: Id(4)] double Y, [property: Id(5)] string? Payload) : Signal;
