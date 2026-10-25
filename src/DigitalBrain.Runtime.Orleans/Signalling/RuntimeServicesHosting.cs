using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace DigitalBrain.Runtime.Orleans.Signalling;

/// <summary>Hosts the runtime services (04-13): the session synapse neuron and the brain listener neuron. <c>AddDigitalBrainRuntime</c> calls it.</summary>
internal static class RuntimeServicesHosting
{
    internal static ISiloBuilder AddRuntimeServices(this ISiloBuilder silo)
    {
        silo.AddNeuronHosting(typeof(RuntimeServicesHosting).Assembly);
        silo.Services.AddOptions<SessionSynapseOptions>().BindConfiguration(IntoChatOSNames.Signalling.Section);
        silo.Services.TryAddSingleton(TimeProvider.System);
        silo.Services.AddSingleton<RuntimeClock>();
        return silo;
    }
}
