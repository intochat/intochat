namespace IntoChat.OS.Modules.Apps.Signals;

[DigitalBrain.Signal("apps.app-published")]
public sealed record AppPublished(
    string Name,
    string Version) : DigitalBrain.Signal;
