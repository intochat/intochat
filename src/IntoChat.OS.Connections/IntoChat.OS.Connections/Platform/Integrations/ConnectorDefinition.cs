using IntoChat.OS.Sdk.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Sdk.Platform.Integrations;

namespace IntoChat.OS.Connections.Platform.Integrations;

public sealed record ConnectorDefinition(string Id, string DisplayName, string[] AuthMethods, string[] RequiredIntegrations)
{
    public string ExternalSystem { get; init; } = DisplayName;
    public string[] OptionalIntegrations { get; init; } = [];
    public Type[] Neurons { get; init; } = [];
    public Type[] Signals { get; init; } = [];
    public OAuthMethod[] OAuthMethods { get; init; } = [];

    // Capability kinds every connection of this connector offers, such as IStorage.Capability.
    public string[] Capabilities { get; init; } = [];

    // A module declares its connectors as a static Connector or Connectors member.
    public static ConnectorDefinition[] DeclaredBy(Type module)
    {
        ArgumentNullException.ThrowIfNull(module);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        return new[] { "Connector", "Connectors" }.SelectMany(name =>
            (module.GetProperty(name, flags)?.GetValue(null) ?? module.GetField(name, flags)?.GetValue(null)) switch
            {
                ConnectorDefinition one => [one],
                IEnumerable<ConnectorDefinition> many => many,
                _ => [],
            }).ToArray();
    }
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
