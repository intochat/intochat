using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.NotificationToast;

[GenerateSerializer, Alias("ui.toast.item")]
public sealed record NotificationToastItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string Message,
    [property: Id(3)] string Severity = "info",
    [property: Id(4)] string? ActionLabel = null,
    [property: Id(5)] string? ActionId = null)
{
    public NotificationToastItem() : this("", "", "", "info", null, null) { }
}

[GenerateSerializer, Alias("ui.toast.state")]
public sealed record NotificationToastState(
    [property: Id(0)] int Version,
    [property: Id(1)] NotificationToastItem[] Items,
    [property: Id(2)] bool IsOpen,
    [property: Id(3)] string? LastInvokedActionId,
    [property: Id(4)] int DismissCount)
{
    public NotificationToastState() : this(0, [], false, null, 0) { }
}

[NeuronAlias("ui.toast")]
[DefaultNeuronType("ui.toast")]
public interface INotificationToast
{
    Task<NotificationToastState> Show(string title, string message, string severity = "info", string? actionLabel = null, string? actionId = null, string? id = null);
    Task<NotificationToastState> InvokeAction(string toastId);
    Task<NotificationToastState> Dismiss(string? toastId = null);
    Task<NotificationToastState> Clear();
    [ReadOnlyCall] Task<NotificationToastState> Read();
}
