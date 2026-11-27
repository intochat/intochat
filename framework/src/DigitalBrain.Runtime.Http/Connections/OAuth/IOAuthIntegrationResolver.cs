using DigitalBrain.Capabilities.Connections.Integrations;

namespace DigitalBrain.Runtime.Http.Connections.OAuth;

/// <summary>Resolves a platform integration handle by id for OAuth client credentials.</summary>
public interface IOAuthIntegrationResolver
{
    IIntegrationHandle Get(string integrationId);
}
