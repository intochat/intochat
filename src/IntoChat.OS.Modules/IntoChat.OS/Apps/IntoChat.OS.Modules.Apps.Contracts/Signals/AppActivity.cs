namespace IntoChat.OS.Modules.Apps.Signals;

// What an app is doing right now, for whoever renders its presence. State is one of States.
[DigitalBrain.Signal("apps.app-activity")]
public sealed record AppActivity(
    string App,
    string State,
    string Detail) : DigitalBrain.Signal
{
    public static readonly string[] States =
        ["idle", "listening", "accepted", "working", "reading", "searching", "waiting", "completed", "attention", "disconnected"];
}
