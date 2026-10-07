namespace DigitalBrain.Framework.Platform.Integrations;

public sealed record ConnectorDefinition(string Id, string DisplayName, string[] AuthMethods, string[] RequiredIntegrations)
{
    public string ExternalSystem { get; init; } = DisplayName;
    public string[] OptionalIntegrations { get; init; } = [];
    public Type[] Neurons { get; init; } = [];
    public Type[] Signals { get; init; } = [];
}

public interface IIntegrationHandle
{
    string IntegrationId { get; }
    Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default);
    Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default);
}

public interface IModuleIntegrations<TModule> where TModule : Module
{
    IIntegrationHandle Get(string integrationId);
}

// Platform runs these after Integration seeding and before its startup task completes.
public interface IIntegrationStartup
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IModuleConnectionCredentials<TModule> where TModule : Module
{
    Task<SecretRef> WriteSecretAsync(string connectionKey, string field, string displayName, string value, CancellationToken cancellationToken = default, string? targetBrainId = null);
    Task<string> ReadConnectionAsync(string connectorId, string connectionId, CancellationToken cancellationToken = default);
    Task ConnectAsync(string connectionKey, string authMethodId, SecretRef credential, CancellationToken cancellationToken = default, string? targetBrainId = null);
    Task<bool> IsConnectedAsync(string connectionKey, CancellationToken cancellationToken = default);
}

public static class ConnectionKeys
{
    public static string For(string brainId, string connectorId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return $"connection/{Uri.EscapeDataString(brainId)}/{Uri.EscapeDataString(connectorId)}/{Uri.EscapeDataString(connectionId)}";
    }

    public static (string BrainId, string ConnectorId, string ConnectionId) Parse(string key)
    {
        if (key.Split('/') is not ["connection", var brain, var connector, var connection])
        { throw new ArgumentException("A connection grain key is required.", nameof(key)); }
        var result = (Uri.UnescapeDataString(brain), Uri.UnescapeDataString(connector), Uri.UnescapeDataString(connection));
        if (For(result.Item1, result.Item2, result.Item3) != key) { throw new ArgumentException("A canonical connection key is required.", nameof(key)); }
        return result;
    }
}
