using System.Text.Json.Serialization;

namespace DigitalBrain.Framework.Platform.Integrations.Accounts;

// An account that lives outside the vault-backed registry (an OAuth grant held by its own neuron).
// The integration's module contributes it so the accounts surface stays integration-neutral.
public interface IExternalConnection
{
    string ConnectionId { get; }

    Task<ConnectionRow?> ReadAsync(IGrainFactory grains, CancellationToken cancellationToken);

    Task DisconnectAsync(IGrainFactory grains, CancellationToken cancellationToken);
}

public sealed record ConnectionRow(
    string Id,
    string ConnectorId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastProbedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Account = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AuthMethodId = null);
