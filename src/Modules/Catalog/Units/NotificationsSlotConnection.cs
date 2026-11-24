using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Unit builds compiled before INotifications moved to IntoChat.OS.Modules.Catalog.Contracts name it in this assembly; stored
// builds keep loading through this forwarder.
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(IntoChat.OS.Modules.Apps.Notifications.INotifications))]

namespace IntoChat.OS.Modules.Apps.Notifications;

[GenerateSerializer, Alias("apps.notifications-slot-connection-state")]
internal sealed record NotificationsSlotConnectionState : ConnectionState
{
    [Id(0)] public bool Enabled { get; set; } = true;
}

/// <summary>
/// Brain-scoped connection neuron providing <c>notifications@1</c> backed by the brain's durable <c>notifications.inbox</c> neuron.
/// </summary>
[Neuron(TypeName, State = "notifications")]
internal sealed class NotificationsSlotConnection : ConnectionNeuron<NotificationsSlotConnectionState>, INotifications
{
    public const string TypeName = "notifications.connection";

    private static readonly ILogger ConsoleLogger = IntoChat.OS.Host.Logging.OsConsoleLog.For<NotificationsSlotConnection>();

    private readonly ILogger _logger;

    public NotificationsSlotConnection() : this(null) { }

    [ActivatorUtilitiesConstructor]
    public NotificationsSlotConnection(ILogger<NotificationsSlotConnection>? logger = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
    }

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
            ct).ConfigureAwait(true);
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
