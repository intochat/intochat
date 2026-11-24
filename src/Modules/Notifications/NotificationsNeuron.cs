using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.Sdk;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Notifications;

[Neuron(INotificationsInbox.TypeName)]
internal sealed class NotificationsNeuron(IOptions<NotificationsOptions>? options = null)
    : Neuron<NotificationsState>, INotificationsInbox
{
    private const int DefaultMaxItems = 200;

    public async Task<NotificationReceipt> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);

        var dedupeKey = string.IsNullOrWhiteSpace(request.DedupeKey) ? null : request.DedupeKey.Trim();
        if (dedupeKey is not null)
        {
            var existing = Array.Find(State.Items, entry => string.Equals(entry.DedupeKey, dedupeKey, StringComparison.Ordinal));
            if (existing is not null)
            {
                return new NotificationReceipt(existing.Item.Id, Deduplicated: true, existing.Item.CreatedUtc);
            }
        }

        var now = Context.Time.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        var sourceApp = ResolveSourceApp();
        var title = request.Title.Trim();
        var body = (request.Body ?? string.Empty).Trim();
        var category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        var actionUrl = string.IsNullOrWhiteSpace(request.ActionUrl) ? null : request.ActionUrl.Trim();

        var item = new NotificationItem(
            Id: id,
            BrainId: Id.Brain.Value,
            SourceApp: sourceApp,
            Title: title,
            Body: body,
            Priority: request.Priority,
            Category: category,
            ActionUrl: actionUrl,
            Read: false,
            CreatedUtc: now,
            ReadUtc: null);

        var maxItems = Math.Max(1, options?.Value.MaxItems ?? DefaultMaxItems);
        StoredNotification[] nextItems = [new StoredNotification { Item = item, DedupeKey = dedupeKey }, .. State.Items];
        if (nextItems.Length > maxItems)
        {
            nextItems = nextItems[..maxItems];
        }

        await Save(
            new NotificationsState { Items = nextItems },
            new NotificationSent(id, sourceApp, title, request.Priority, now)).ConfigureAwait(true);

        return new NotificationReceipt(id, Deduplicated: false, now);
    }

    public Task<NotificationReceipt> NotifyAsync(
        string title,
        string body,
        NotificationPriority priority = NotificationPriority.Normal,
        CancellationToken cancellationToken = default)
        => SendAsync(new NotificationRequest(title, body, priority), cancellationToken);

    public Task<NotificationItem[]> ListAsync(int limit = 50, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0 || State.Items.Length == 0)
        {
            return Task.FromResult<NotificationItem[]>([]);
        }

        var query = unreadOnly
            ? State.Items.Where(entry => !entry.Item.Read)
            : State.Items;

        var items = query
            .Take(limit)
            .Select(entry => entry.Item)
            .ToArray();

        return Task.FromResult(items);
    }

    public async Task MarkReadAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        var index = Array.FindIndex(State.Items, entry => string.Equals(entry.Item.Id, notificationId, StringComparison.Ordinal));
        if (index < 0 || State.Items[index].Item.Read)
        {
            return;
        }

        var now = Context.Time.GetUtcNow();
        var nextItems = (StoredNotification[])State.Items.Clone();
        var current = nextItems[index];
        nextItems[index] = current with
        {
            Item = current.Item with { Read = true, ReadUtc = now },
        };

        await Store(new NotificationsState { Items = nextItems }).ConfigureAwait(true);
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Array.Exists(State.Items, entry => !entry.Item.Read))
        {
            return;
        }

        var now = Context.Time.GetUtcNow();
        var nextItems = new StoredNotification[State.Items.Length];
        for (var i = 0; i < State.Items.Length; i++)
        {
            var entry = State.Items[i];
            nextItems[i] = entry.Item.Read
                ? entry
                : entry with { Item = entry.Item with { Read = true, ReadUtc = now } };
        }

        await Store(new NotificationsState { Items = nextItems }).ConfigureAwait(true);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State.Items.Length == 0)
        {
            return;
        }

        await Store(new NotificationsState { Items = [] }).ConfigureAwait(true);
    }

    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = 0;
        foreach (var entry in State.Items)
        {
            if (!entry.Item.Read)
            {
                count++;
            }
        }

        return Task.FromResult(count);
    }

    private string ResolveSourceApp()
    {
        if (Context.Caller?.Actor is { } actor && actor is not Actor.Kernel)
        {
            return actor switch
            {
                Actor.App app when !string.IsNullOrWhiteSpace(app.Key) => app.Key,
                Actor.Agent => "assistant",
                Actor.Person => "user",
                Actor.Scheduler => "scheduler",
                Actor.Module module when !string.IsNullOrWhiteSpace(module.Id) => module.Id,
                _ => "os",
            };
        }

        return "os";
    }
}
