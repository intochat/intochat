using DigitalBrain.Neurons;
using System.Reflection;
using DigitalBrain.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans;
using Orleans.Hosting;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>The internal registration of the host grains' type map. AddDigitalBrainRuntime (04-11) calls it; it is not a public entry.</summary>
internal static class NeuronHosting
{
    /// <summary>Hosts the plain <see cref="NeuronAttribute"/> classes of <paramref name="assemblies"/>.</summary>
    internal static ISiloBuilder AddNeuronHosting(this ISiloBuilder silo, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(assemblies);
        RuntimeProtocol.Register(Signalling.RuntimeOrleansJsonContext.Default);
        foreach (var assembly in assemblies) { silo.Services.AddSingleton(new HostedAssembly(assembly)); }
        silo.Services.AddSingleton(new NeuronBinding(typeof(HostedCaller), static (_, id) => new HostedCaller(id)));
        silo.Services.TryAddSingleton(sp => new NeuronTypeRegistry(
            sp.GetServices<HostedAssembly>()
                .Select(hosted => hosted.Assembly)
                .Concat(Capabilities.CapabilityHosting.RegisteredPortAssemblies(sp))
                .Distinct(),
            TopologyHosting.ComposedModules(sp)));
        silo.Services.TryAddSingleton<DigitalBrain.Components.IHostedNeuronTypes>(sp => sp.GetRequiredService<NeuronTypeRegistry>());
        return silo;
    }

    /// <summary>A typed proxy, from outside any neuron (or from the neuron bound into <paramref name="services"/>), to the neuron <paramref name="id"/> hosted in the silo of <paramref name="services"/>.</summary>
    internal static T Get<T>(IServiceProvider services, NeuronId id) where T : class
        => NeuronProxy.Create<T>(id, Sink(services));

    /// <summary>A proxy for a contract known only at runtime.</summary>
    internal static object Get(IServiceProvider services, Type contract, NeuronId id)
        => NeuronProxy.Create(contract, id, Sink(services));

    private static OrleansInvocationSink Sink(IServiceProvider services)
        => new(services.GetRequiredService<IGrainFactory>(), services.GetService<NeuronTypeRegistry>(),
            services.GetService<HostedCaller>()?.Id,
            Access.OrleansCallerAmbient.Instance, asKernel: false,
            services.GetService<NeuronStreamAuthorizer>() is { } gate ? (target, call) => gate.Authorize(services, target, call) : null,
            transport: services.GetService<IBrainTransport>());

    /// <summary>A typed proxy executing under kernel authority in the target's brain.</summary>
    internal static T AsKernel<T>(IServiceProvider services, NeuronId id) where T : class
        => NeuronProxy.Create<T>(id, new OrleansInvocationSink(services.GetRequiredService<IGrainFactory>(), services.GetService<NeuronTypeRegistry>(),
            caller: null, Access.OrleansCallerAmbient.Instance, asKernel: true));

    internal sealed record HostedAssembly(Assembly Assembly);

    internal sealed record HostedCaller(NeuronId Id);
}
