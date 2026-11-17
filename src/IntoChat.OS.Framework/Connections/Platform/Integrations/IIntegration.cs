using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Integrations;
using Orleans.Concurrency;

namespace IntoChat.OS.Connections.Platform.Integrations;

// Key: "integration/{id}". One deployment-level provider registration (a Google OAuth app, an AI key);
// every value lives in the vault and never appears in state, snapshots or signals.
[Alias("integration.registration")]
[Orleans.Metadata.DefaultGrainType("integration.registration")]
public interface IIntegration : IGrainWithStringKey
{
    [ReadOnly]
    Task<IntegrationSnapshot> Read();

    Task<IntegrationSnapshot> Configure(ConfigureIntegration request);

    Task<IntegrationSnapshot> Clear(string field);

    // Reconciles deployment-provided values: applies a field when it is absent from state, or when the
    // stored value also came from the deployment and now differs. A value a person set or cleared is
    // never touched.
    Task<IntegrationSnapshot> Seed(ConfigureIntegration request);

    Task<ReleasedIntegration> Release();
}
