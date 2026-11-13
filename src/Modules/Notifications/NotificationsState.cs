using DigitalBrain.State;
using IntoChat.Sdk;

namespace IntoChat.OS.Modules.Notifications;

[GenerateSerializer, Alias("notifications.record")]
internal sealed record StoredNotification
{
    [Id(0)] public NotificationItem Item { get; init; } = null!;
    [Id(1)] public string? DedupeKey { get; init; }
}

[GenerateSerializer, Alias("notifications.inbox-state")]
[ExportableState]
internal sealed record NotificationsState
{
    [Id(0)] public StoredNotification[] Items { get; init; } = [];
}
