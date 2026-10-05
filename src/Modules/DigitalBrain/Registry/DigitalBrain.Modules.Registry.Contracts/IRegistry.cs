using DigitalBrain;

namespace DigitalBrain.Registry;

[Alias("registry"), Orleans.Metadata.DefaultGrainType("registry")]
public interface IRegistry : INeuron
{
    const string Key = "registry";

    Task<RegistryDiscovery> Discover(string query, CancellationToken cancellationToken = default);
    Task<RegistryDiscovery> Browse(string query, string? provider = null, int offset = 0, int limit = 10, CancellationToken cancellationToken = default);
    Task<RegistryCapability?> Select(string id, CancellationToken cancellationToken = default);

}

