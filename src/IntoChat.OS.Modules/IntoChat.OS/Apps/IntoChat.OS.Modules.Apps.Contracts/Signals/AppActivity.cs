namespace IntoChat.OS.Modules.Apps.Signals;

// What an app is doing right now, for whoever renders its presence. State is one of States.
[GenerateSerializer, Alias("apps.app-activity")]
public sealed record AppActivity(
    [property: Id(0)] string App,
    [property: Id(1)] string State,
    [property: Id(2)] string Detail) : Signal
{
    public static readonly string[] States =
        ["idle", "listening", "accepted", "working", "reading", "searching", "waiting", "completed", "attention", "disconnected"];
}
