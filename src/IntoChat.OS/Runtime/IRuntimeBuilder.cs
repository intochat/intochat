// os-only: the os module composition seam; the framework composes differently.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Runtime;

// Module composition without ISiloBuilder. Modules move to it in PR 8a.
public interface IRuntimeBuilder
{
    IServiceCollection Services { get; }

    IConfiguration Configuration { get; }

    // TImplementation is a neuron or runtime-endpoint class.
    IRuntimeBuilder AddNeuron<TImplementation>() where TImplementation : class;

    IRuntimeBuilder AddStateStore(string name, Func<IServiceProvider, INeuronStateStore> store);

    // The provider's own builder (ISiloBuilder is reachable only inside the Orleans provider).
    bool TryGetProvider<TBuilder>(out TBuilder? builder) where TBuilder : class;
}
