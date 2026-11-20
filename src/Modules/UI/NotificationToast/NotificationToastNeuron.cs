using IntoChat.OS.Modules.UI.Core;
using System.Globalization;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.NotificationToast.Signals;

namespace IntoChat.OS.Modules.UI.NotificationToast;

[Neuron("ui.toast")]
internal sealed class NotificationToastNeuron : UiNeuronBase<NotificationToastState>, INotificationToast
{
    private const int MaxToasts = 10;

    public async Task<NotificationToastState> Show(string title, string message, string severity = "info", string? actionLabel = null, string? actionId = null, string? id = null)
    {
        var nextVersion = State.Version + 1;
        var toastId = string.IsNullOrWhiteSpace(id)
            ? string.Create(CultureInfo.InvariantCulture, $"toast-{nextVersion}")
            : id.Trim();

        var item = new NotificationToastItem(
            toastId,
            string.IsNullOrWhiteSpace(title) ? "Notification" : title.Trim(),
            message?.Trim() ?? "",
            NormalizeSeverity(severity),
            string.IsNullOrWhiteSpace(actionLabel) ? null : actionLabel.Trim(),
            string.IsNullOrWhiteSpace(actionId) ? (string.IsNullOrWhiteSpace(actionLabel) ? null : toastId) : actionId.Trim());

        var list = State.Items
            .Where(t => !string.Equals(t.Id, toastId, StringComparison.Ordinal))
            .Prepend(item)
            .Take(MaxToasts)
            .ToArray();

        var next = State with
        {
            Version = nextVersion,
            Items = list,
            IsOpen = list.Length > 0,
        };
        await Save(next, new NotificationToastChanged(Id.FullKey(), next.Version, next.IsOpen, next.LastInvokedActionId));
        return next;
    }

    public async Task<NotificationToastState> InvokeAction(string toastId)
    {
        var item = State.Items.FirstOrDefault(t => string.Equals(t.Id, toastId, StringComparison.Ordinal));
        if (item is null)
        {
            return State;
        }

        var remaining = State.Items.Where(t => !string.Equals(t.Id, toastId, StringComparison.Ordinal)).ToArray();
        var action = item.ActionId ?? item.Id;
        var next = State with
        {
            Version = State.Version + 1,
            Items = remaining,
            IsOpen = remaining.Length > 0,
            LastInvokedActionId = action,
        };
        await Save(next, new NotificationToastChanged(Id.FullKey(), next.Version, next.IsOpen, next.LastInvokedActionId));
        return next;
    }

    public async Task<NotificationToastState> Dismiss(string? toastId = null)
    {
        if (State.Items.Length == 0)
        {
            return State;
        }

        var targetId = string.IsNullOrWhiteSpace(toastId) ? State.Items[0].Id : toastId.Trim();
        var remaining = State.Items.Where(t => !string.Equals(t.Id, targetId, StringComparison.Ordinal)).ToArray();
        var next = State with
        {
            Version = State.Version + 1,
            Items = remaining,
            IsOpen = remaining.Length > 0,
            DismissCount = State.DismissCount + 1,
        };
        await Save(next, new NotificationToastChanged(Id.FullKey(), next.Version, next.IsOpen, next.LastInvokedActionId));
        return next;
    }

    public async Task<NotificationToastState> Clear()
    {
        var next = State with
        {
            Version = State.Version + 1,
            Items = [],
            IsOpen = false,
        };
        await Save(next, new NotificationToastChanged(Id.FullKey(), next.Version, false, next.LastInvokedActionId));
        return next;
    }

    public Task<NotificationToastState> Read() => Task.FromResult(State);

    private static string NormalizeSeverity(string? severity) => (severity ?? "info").Trim().ToLowerInvariant() switch
    {
        "success" or "done" => "success",
        "warning" or "caution" => "warning",
        "error" or "danger" or "critical" => "error",
        _ => "info",
    };
}
