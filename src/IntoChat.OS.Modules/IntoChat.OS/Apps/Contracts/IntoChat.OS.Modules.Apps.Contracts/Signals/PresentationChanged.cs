namespace IntoChat.OS.Modules.Apps.Signals;

[DigitalBrain.Signal("apps.presentation-changed")]
public sealed record PresentationChanged(string App, long ProgramRevision, long Revision) : DigitalBrain.Signal;
