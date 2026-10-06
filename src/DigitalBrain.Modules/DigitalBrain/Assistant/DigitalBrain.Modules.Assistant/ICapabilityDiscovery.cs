using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Assistant;

[PlatformOnly, Alias("assistant.discovery"), Orleans.Metadata.DefaultGrainType("assistant.discovery")]
public interface ICapabilityDiscovery : INeuron
{
    Task<RegistryDiscovery> Discover(string query, CancellationToken cancellationToken = default);
    Task<RegistryDiscovery> Browse(string query, string? provider = null, int offset = 0, int limit = 10, CancellationToken cancellationToken = default);
    Task<RegistryCapability?> Select(string id, CancellationToken cancellationToken = default);
}
