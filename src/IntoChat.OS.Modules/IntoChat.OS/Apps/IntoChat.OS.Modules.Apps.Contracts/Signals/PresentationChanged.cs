namespace IntoChat.OS.Modules.Apps.Signals;

[GenerateSerializer, Alias("apps.presentation-changed")]
public sealed record PresentationChanged([property: Id(0)] string App, [property: Id(1)] long ProgramRevision, [property: Id(2)] long Revision) : Signal;
