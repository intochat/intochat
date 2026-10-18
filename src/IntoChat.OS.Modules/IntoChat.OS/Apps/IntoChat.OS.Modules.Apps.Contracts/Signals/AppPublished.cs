namespace IntoChat.OS.Apps.Signals;

[GenerateSerializer, Alias("apps.app-published")]
public sealed record AppPublished(
    [property: Id(0)] string Name,
    [property: Id(1)] string Version) : Signal;
