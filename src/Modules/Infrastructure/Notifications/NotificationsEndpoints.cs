using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using IntoChat.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;

namespace IntoChat.OS.Modules.Notifications;

internal sealed record NotificationListResponse(NotificationItem[] Items, int UnreadCount);

internal static class NotificationsEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = BrainRouteGroups.Brain(endpoints, "/notifications")
            .AddEndpointFilter(ModuleRouteGuard.Guard);

        group.MapGet("", async (INeuronProxies brain, CancellationToken ct, int limit = 50, bool unreadOnly = false) =>
        {
            var inbox = Inbox(brain);
            var items = await inbox.ListAsync(limit, unreadOnly, ct).ConfigureAwait(false);
            var unreadCount = await inbox.UnreadCountAsync(ct).ConfigureAwait(false);
            return Results.Ok(new NotificationListResponse(items, unreadCount));
        });

        group.MapPost("", async (NotificationRequest request, INeuronProxies brain, CancellationToken ct) =>
        {
            var receipt = await Inbox(brain).SendAsync(request, ct).ConfigureAwait(false);
            return Results.Ok(receipt);
        });

        group.MapPost("/{id}/read", async (string id, INeuronProxies brain, CancellationToken ct) =>
        {
            await Inbox(brain).MarkReadAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        group.MapPost("/read-all", async (INeuronProxies brain, CancellationToken ct) =>
        {
            await Inbox(brain).MarkAllReadAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        group.MapPost("/clear", async (INeuronProxies brain, CancellationToken ct) =>
        {
            await Inbox(brain).ClearAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        group.MapDelete("", async (INeuronProxies brain, CancellationToken ct) =>
        {
            await Inbox(brain).ClearAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    private static INotificationsInbox Inbox(INeuronProxies brain)
        => brain.Get<INotificationsInbox>(INotificationsInbox.Key(BrainScope.CurrentId()));
}
