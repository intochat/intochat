using DigitalBrain;

namespace IntoChat.OS.Modules.UI.NotificationToast.Signals;

[Signal("ui.toast-changed")]
public sealed record NotificationToastChanged(string ToastNeuronId, int Version, bool IsOpen, string? LastInvokedActionId) : Signal;
