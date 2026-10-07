using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Framework.Platform.Integrations;

public sealed record ConnectorDefinition(string Id, string DisplayName, string[] AuthMethods, string[] RequiredIntegrations)
{
    public string ExternalSystem { get; init; } = DisplayName;
    public string[] OptionalIntegrations { get; init; } = [];
    public Type[] Neurons { get; init; } = [];
    public Type[] Signals { get; init; } = [];
    public OAuthMethod[] OAuthMethods { get; init; } = [];
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
