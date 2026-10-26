namespace IntoChat.OS.Modules.Apps.Signals;

[DigitalBrain.Signal("apps.app-changed2")]
public sealed record AppChanged(
    string Name,
    string Version,
    AppStatus Status) : DigitalBrain.Signal;
