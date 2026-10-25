using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Video.Signals;

[GenerateSerializer, Alias("ui.video-ended")]
public sealed record VideoEnded([property: Id(0)] string Name) : Signal;
