namespace IntoChat.OS.Apps.Signals;

[GenerateSerializer, Alias("apps.app-changed2")]
public sealed record AppChanged(
    [property: Id(0)] string Name,
    [property: Id(1)] string Version,
    [property: Id(2)] AppStatus Status) : Signal;
