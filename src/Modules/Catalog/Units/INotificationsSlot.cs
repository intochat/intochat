using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

[GenerateSerializer, Alias("apps.notifications-slot-connection-state")]
public sealed record NotificationsSlotConnectionState : ConnectionState
{
    [Id(0)] public bool Enabled { get; set; } = true;
}

/// <summary>
/// Brain-scoped connection neuron providing <c>notifications@1</c> backed by the brain's durable <c>notifications.inbox</c> neuron.
/// </summary>
[Neuron(TypeName, State = "notifications")]
public sealed class NotificationsSlotConnection(ILogger<NotificationsSlotConnection>? logger = null)
    : ConnectionNeuron<NotificationsSlotConnectionState>, INotifications
{
    public const string TypeName = "notifications.connection";
    private readonly ILogger _logger = logger ?? NullLogger<NotificationsSlotConnection>.Instance;

    public async Task<JsonElement> Notify(string title, string body, string priority, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ct.ThrowIfCancellationRequested();

        var parsedPriority = (priority ?? "normal").Trim().ToLowerInvariant() switch
        {
            "low" => NotificationPriority.Low,
            "high" => NotificationPriority.High,
            "urgent" => NotificationPriority.Urgent,
            _ => NotificationPriority.Normal,
        };

        if (Context is null)
        {
            throw new InvalidOperationException("Notifications slot connection is not attached to a brain context.");
        }

        _logger.LogDebug("Delivering unit notification '{Title}' to notifications.inbox.", title.Trim());
        var inbox = Context.Get<IntoChat.Sdk.INotifications>(
            new NeuronId(Id.Brain, "notifications.inbox", "inbox"));
        var receipt = await inbox.SendAsync(
            new NotificationRequest(title.Trim(), body ?? string.Empty, parsedPriority),
            ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            id = receipt.Id,
            deduplicated = receipt.Deduplicated,
            createdUtc = receipt.CreatedUtc,
        });
    }

    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct) =>
        Task.FromResult(ConnectionProbeResult.Connected("Brain notifications inbox ready."));
}
