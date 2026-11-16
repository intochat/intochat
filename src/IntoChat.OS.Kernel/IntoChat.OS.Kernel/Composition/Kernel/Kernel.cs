using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Kernel.Composition;

// Reads the directory of the stamped caller's brain; works on a silo and on a cluster client alike.
public sealed class Kernel(IGrainFactory grains, IBrainMembership? membership = null, IServiceProvider? services = null) : IKernel
{
    // The kernel over the grain factory of services (a silo, or a cluster client host such as the MCP), with its brain
    // membership when the host registers one.
    public static Kernel For(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IGrainFactory>(services),
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<IBrainMembership>(services),
            services);
    }

    public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken = default) where T : class
        => Instances(TypeOf(typeof(T)), cancellationToken);

    public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
        => InstancesIn(BrainScope.CurrentId(), type, cancellationToken);

    public async IAsyncEnumerable<BrainInstance> InstancesIn(string brain, string? type, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        string[] types = type is null ? await grains.GetGrain<IInstanceTypes>(brain).List().WaitAsync(cancellationToken) : [type];
        foreach (var each in types)
        {
            foreach (var instance in await grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(brain, each)).List().WaitAsync(cancellationToken))
            { yield return instance; }
        }
    }

    // A call with no caller is in no brain, so it sees the compiled layer alone.
    public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
        => (RuntimeCallerContext.TryGet(out _)
            ? grains.GetGrain<ITypeTable>(BrainScope.CurrentId()).List()
            : grains.GetGrain<ICompiledTypeTable>(TypeTableKey.NoBrain).List()).WaitAsync(cancellationToken);

    public async Task<string[]> Brains(CancellationToken cancellationToken = default)
    {
        var current = BrainScope.CurrentId();
        var caller = RuntimeCallerContext.Require();
        // Fail closed: without a membership source the kernel cannot tell which brains the caller may see.
        var source = membership ?? throw new InvalidOperationException("No brain membership source registered: register IBrainMembership (AddIntoChatOSConnections) before listing brains.");
        var members = await source.BrainsOf(caller.PersonId, cancellationToken).WaitAsync(cancellationToken);
        return members.Contains(current, StringComparer.Ordinal) ? members : [current, .. members];
    }

    public Task<ObservedSynapse[]> Synapses(CancellationToken cancellationToken = default)
        => SynapseObservation.List(services, BrainScope.CurrentId(), cancellationToken);

    public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default)
        => SynapseObservation.Deliveries(services, BrainScope.CurrentId(), cancellationToken);

    public Task<BrainInstance[]> Running(CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<BrainInstance>());

    public Task Retire(string type, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var brain = BrainScope.CurrentId();
        if (!BrainKey.BelongsTo(key, brain)) { throw new UnauthorizedAccessException($"'{key}' is not in the caller's brain."); }
        return grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(brain, type)).Retire(key).WaitAsync(cancellationToken);
    }

    public async Task<System.Text.Json.JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default)
    {
        var host = grains.GetGrain<DigitalBrain.Runtime.Orleans.Hosting.INeuronHost>(id.ToString());
        if (host.GetType().GetMethod("InspectState", [typeof(CancellationToken)]) is { } inspectState)
        {
            var task = (Task<System.Text.Json.JsonElement?>)inspectState.Invoke(host, [cancellationToken])!;
            return await task.ConfigureAwait(false);
        }
        return null;
    }

    public Task<Feature[]> Features(string brain, CancellationToken cancellationToken = default)
        => TypeTable(brain).Features().WaitAsync(cancellationToken);

    public Task LoadFeature(string brain, Feature feature, BrainType[] types, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(types);
        return TypeTable(brain).Load(feature, types).WaitAsync(cancellationToken);
    }

    public Task UnloadFeature(string brain, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return TypeTable(brain).Unload(name).WaitAsync(cancellationToken);
    }

    public async Task CancelFlow(BrainId brain, NeuronId flowId, string? operationId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var edge = services?.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
        using var _ = edge?.EnterKernel(brain);
        if (operationId is { Length: > 0 } id && services is not null)
        {
            var operationsQuery = services.GetService(typeof(DigitalBrain.Operations.IOperationsQuery)) as DigitalBrain.Operations.IOperationsQuery;
            var operations = services.GetService(typeof(DigitalBrain.Operations.IOperations)) as DigitalBrain.Operations.IOperations;
            var operation = new DigitalBrain.Operations.OperationId(id);
            if (operationsQuery is not null && operations is not null
                && await operationsQuery.Read(operation, cancellationToken).ConfigureAwait(false) is { Result: null })
            {
                await operations.Cancel(operation, cancellationToken).ConfigureAwait(false);
            }
        }
        if (services is not null)
        {
            await DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons
                .Get<IntoChat.OS.Modules.Apps.Flows.IFlowStarter>(services, flowId)
                .Cancel(reason)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private ITypeTable TypeTable(string brain)
    {
        if (!BrainScope.IsValidId(brain)) { throw new ArgumentException($"'{brain}' is not a brain id.", nameof(brain)); }
        return grains.GetGrain<ITypeTable>(brain);
    }

    // A contract's default grain type; a contract several grains implement has none and is refused.
    public string TypeOf(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (DigitalBrain.Contracts.PlainNeuronContracts.TypeOf(contract) is { } plain) { return plain; }
        try { return grains.GetGrain(contract, "_").GetGrainId().Type.ToString()!; }
        catch (ArgumentException error) { throw new ArgumentException($"{contract.Name} has no single grain type: {error.Message}", nameof(contract), error); }
    }

    // The grain type of a still-grain's contract (CompiledTypes.Addressed), or null when several grains or none implement it.
    internal static string? StillGrainType(IGrainFactory grains, Type contract)
    {
        try { return grains.GetGrain(contract, "_").GetGrainId().Type.ToString(); }
        catch (ArgumentException) { return null; }
    }
}
