using DigitalBrain;

namespace IntoChat.OS.Modules.UI.AutoSuggestBox.Signals;

[Signal("ui.auto-suggest-changed")]
public sealed record AutoSuggestChanged(string AutoSuggestId, int Version, string Query, string? SubmittedText) : Signal;
