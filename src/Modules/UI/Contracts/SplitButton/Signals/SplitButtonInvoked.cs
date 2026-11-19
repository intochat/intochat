using DigitalBrain;

namespace IntoChat.OS.Modules.UI.SplitButton.Signals;

[Signal("ui.split-button-invoked")]
public sealed record SplitButtonInvoked(string ButtonId, int Version, string ActionId, int InvokeCount) : Signal;
