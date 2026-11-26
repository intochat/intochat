using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Notifications;

/// <summary>
/// Sandboxed notification slot capability (<c>notifications@1</c>) exposed to runtime synapse units
/// via <c>[Slot] INotifications</c> or <c>[Slot] Asks&lt;INotifications&gt;</c>.
/// Uses only <see cref="string"/>, <see cref="CancellationToken"/>, and <see cref="JsonElement"/> so it passes
/// closed-world unit PE metadata verification.
/// </summary>
[Capability("notifications", 1)]
[NeuronAlias("notifications.slot")]
public interface INotifications
{
    /// <summary>Enqueues a notification in the brain's notification inbox.</summary>
    /// <param name="title">Short notification headline.</param>
    /// <param name="body">Notification message body.</param>
    /// <param name="priority">Severity (<c>low</c>, <c>normal</c>, <c>high</c>, <c>urgent</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON receipt (<c>{ "id": "...", "deduplicated": false }</c>).</returns>
    Task<JsonElement> Notify(string title, string body, string priority, CancellationToken ct);
}
