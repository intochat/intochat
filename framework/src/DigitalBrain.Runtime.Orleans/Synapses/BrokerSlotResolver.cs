using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Access;
using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Units;
using Microsoft.Extensions.DependencyInjection;
using SlotUnboundException = DigitalBrain.Units.SlotUnboundException;

namespace DigitalBrain.Runtime.Orleans.Synapses;

/// <summary>
/// The slot resolver of the silo: a slot's binding in the install record of its alias resolves to either a local/offered
/// neuron proxy via <see cref="INeuronProxies"/> or a connection key that the brain's <see cref="ICapabilityConnections"/>
/// lists as connected and providing the slot's capability (bound for the alias as caller app and wrapped by every
/// <see cref="ICapabilityBindingInterceptor"/>). Any miss fails closed with <see cref="SlotUnboundException"/>.
/// </summary>
internal sealed class BrokerSlotResolver(IServiceProvider services) : ISlotResolver
{
    private static readonly MethodInfo BindOpen = typeof(BrokerSlotResolver).GetMethod(nameof(Bind), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly ConcurrentDictionary<Type, MethodInfo> BindClosed = new();
    private static readonly MethodInfo ProxyOpen = typeof(INeuronProxies).GetMethod(nameof(INeuronProxies.Get))!;
    private static readonly ConcurrentDictionary<Type, MethodInfo> ProxyClosed = new();

    /// <inheritdoc />
    public async Task<object> Resolve(NeuronId state, SlotDescriptor slot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var proxies = services.GetService<INeuronProxies>() ?? new OrleansNeuronProxies(services);
        var installs = proxies.Get<IInstalls>(new NeuronId(state.Brain, OrleansInstallHooks.InstallsType, "default"));
        // The slot is resolved for the unit, as the brain's platform: the activating caller may belong to another brain.
        InstallRecord? record;
        using (KernelHop.Enter(state.Brain))
        { record = await installs.Describe(state.Type, ct).ConfigureAwait(true); }
        if (record is null) { throw new SlotUnboundException(state.Type, slot.Name); }
        var binding = record.EffectiveBindings().FirstOrDefault(b => string.Equals(b.Slot, slot.Name, StringComparison.Ordinal));
        if (binding is null) { throw new SlotUnboundException(state.Type, slot.Name); }

        return binding.Target switch
        {
            BindingTarget.Local(var neuron) => ResolveProxy(proxies, state, slot, neuron),
            BindingTarget.Offered(_, var neuron) => ResolveProxy(proxies, state, slot, neuron),
            BindingTarget.Connection(var key) => await ResolveConnection(state, slot, key, ct).ConfigureAwait(true),
            _ => throw new SlotUnboundException(state.Type, slot.Name),
        };
    }

    private static object ResolveProxy(INeuronProxies proxies, NeuronId state, SlotDescriptor slot, NeuronId target)
    {
        var get = ProxyClosed.GetOrAdd(slot.Contract, static contract => ProxyOpen.MakeGenericMethod(contract));
        return get.Invoke(proxies, [target]) ?? throw new SlotUnboundException(state.Type, slot.Name);
    }

    private async Task<object> ResolveConnection(NeuronId state, SlotDescriptor slot, string key, CancellationToken ct)
    {
        var (connection, problem) = await Find(services, state, slot, key, ct).ConfigureAwait(true);
        if (problem is not null) { throw new SlotUnboundException(state.Type, slot.Name); }
        var bind = BindClosed.GetOrAdd(slot.Contract, contract => BindOpen.MakeGenericMethod(contract));
        var task = (Task)bind.Invoke(this, [state, connection!, ct])!;
        return await DigitalBrain.Invocation.TaskBoxing.ResultAsync(task, slot.Contract).ConfigureAwait(true)
            ?? throw new SlotUnboundException(state.Type, slot.Name);
    }

    /// <summary>The connection <paramref name="key"/> of the brain when it is connected and provides the slot's capability, or why not.</summary>
    internal static async Task<(BrainConnection? Connection, string? Problem)> Find(IServiceProvider services, NeuronId state, SlotDescriptor slot, string key, CancellationToken ct)
    {
        if (services.GetService<ICapabilityConnections>() is not { } connections) { return (null, $"slot '{slot.Name}': this host has no capability connections"); }
        var connection = (await connections.ListInOrder(state.Brain, ct).ConfigureAwait(true)).FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal));
        if (connection is null) { return (null, $"slot '{slot.Name}': no connection '{key}'"); }
        if (!connection.Connected) { return (null, $"slot '{slot.Name}': connection '{key}' is not connected"); }
        return connection.Provides.Any(provided => provided.Satisfies(slot.Capability))
            ? (connection, null)
            : (null, $"slot '{slot.Name}': connection '{key}' does not provide {slot.Capability}");
    }

    private async Task<TCapability> Bind<TCapability>(NeuronId state, BrainConnection connection, CancellationToken ct) where TCapability : class
    {
        var implementation = await services.GetRequiredService<ICapabilityConnections>().Bind<TCapability>(state.Brain, connection.Key, state.Type, ct).ConfigureAwait(true);
        foreach (var interceptor in services.GetServices<ICapabilityBindingInterceptor>()) { implementation = interceptor.Wrap(state.Brain, connection, state.Type, implementation); }
        return implementation;
    }
}
