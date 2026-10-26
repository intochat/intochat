using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>
/// Reaches a hosted plain neuron from outside any neuron (a host edge or a test harness on an in-process silo): the call goes
/// to the neuron's host grain by the runtime's hosting policy and carries no neuron caller.
/// </summary>
public static class SiloNeurons
{
    /// <summary>A typed proxy to the neuron <paramref name="id"/> hosted in the silo of <paramref name="services"/>.</summary>
    /// <exception cref="InvalidOperationException">The services are not a silo's that hosts plain neurons.</exception>
    public static T Get<T>(IServiceProvider services, NeuronId id) where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<NeuronTypeRegistry>() is null)
        { throw new InvalidOperationException("These services host no plain neurons: start the silo with AddDigitalBrainRuntime."); }
        return NeuronHosting.Get<T>(services, id);
    }
}
