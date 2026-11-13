namespace IntoChat.Sdk;

/// <summary>Severity level for OS notifications.</summary>
public enum NotificationPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3,
}

/// <summary>Payload submitted by apps and modules to emit a brain notification.</summary>
[GenerateSerializer, Alias("sdk.notification-request")]
public sealed record NotificationRequest(
    [property: Id(0)] string Title,
    [property: Id(1)] string Body,
    [property: Id(2)] NotificationPriority Priority = NotificationPriority.Normal,
    [property: Id(3)] string? Category = null,
    [property: Id(4)] string? ActionUrl = null,
    [property: Id(5)] string? DedupeKey = null);

/// <summary>Receipt returned after enqueuing or deduplicating a notification.</summary>
[GenerateSerializer, Alias("sdk.notification-receipt")]
public sealed record NotificationReceipt(
    [property: Id(0)] string Id,
    [property: Id(1)] bool Deduplicated,
    [property: Id(2)] DateTimeOffset CreatedUtc);

/// <summary>Persisted notification item rendered in the OS bar popover and APIs.</summary>
[GenerateSerializer, Alias("sdk.notification-item")]
public sealed record NotificationItem(
    [property: Id(0)] string Id,
    [property: Id(1)] string BrainId,
    [property: Id(2)] string SourceApp,
    [property: Id(3)] string Title,
    [property: Id(4)] string Body,
    [property: Id(5)] NotificationPriority Priority,
    [property: Id(6)] string? Category,
    [property: Id(7)] string? ActionUrl,
    [property: Id(8)] bool Read,
    [property: Id(9)] DateTimeOffset CreatedUtc,
    [property: Id(10)] DateTimeOffset? ReadUtc);

/// <summary>Signal published whenever a new brain notification is recorded.</summary>
[DigitalBrain.Signal("notifications.sent")]
public sealed record NotificationSent(
    string NotificationId,
    string SourceApp,
    string Title,
    NotificationPriority Priority,
    DateTimeOffset OccurredUtc) : DigitalBrain.Signal;

/// <summary>Durable brain notification inbox available to sandboxed apps.</summary>
public interface INotifications
{
    Task<NotificationReceipt> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default);
    Task<NotificationReceipt> NotifyAsync(string title, string body, NotificationPriority priority = NotificationPriority.Normal, CancellationToken cancellationToken = default);
    Task<NotificationItem[]> ListAsync(int limit = 50, bool unreadOnly = false, CancellationToken cancellationToken = default);
    Task MarkReadAsync(string notificationId, CancellationToken cancellationToken = default);
    Task MarkAllReadAsync(CancellationToken cancellationToken = default);
}
