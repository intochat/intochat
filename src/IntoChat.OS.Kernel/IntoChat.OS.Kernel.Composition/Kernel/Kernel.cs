using IntoChat.OS.Framework;
using System.Runtime.CompilerServices;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Kernel.Composition;

// Reads the directory of the stamped caller's brain; works on a silo and on a cluster client alike.
public sealed class Kernel(IGrainFactory grains, IBrainMembership? membership = null) : IKernel
{
    public IAsyncEnumerable<Instance> Instances<T>(CancellationToken cancellationToken = default) where T : INeuron
        => Instances(TypeOf(typeof(T)), cancellationToken);

    public IAsyncEnumerable<Instance> Instances(string? type, CancellationToken cancellationToken = default)
        => InstancesIn(BrainScope.CurrentId(), type, cancellationToken);

    public async IAsyncEnumerable<Instance> InstancesIn(string brain, string? type, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
    public Task<TypeEntry[]> Types(CancellationToken cancellationToken = default)
        => (CallerContextStamper.TryGet(out _)
            ? grains.GetGrain<ITypeTable>(BrainScope.CurrentId()).List()
            : grains.GetGrain<ICompiledTypeTable>(TypeTableKey.NoBrain).List()).WaitAsync(cancellationToken);

    public async Task<string[]> Brains(CancellationToken cancellationToken = default)
    {
        var current = BrainScope.CurrentId();
        var caller = CallerContextStamper.Require();
        // Fail closed: without a membership source the kernel cannot tell which brains the caller may see.
        var source = membership ?? throw new InvalidOperationException("No brain membership source registered: register IBrainMembership (AddIntoChatOSConnections) before listing brains.");
        var members = await source.BrainsOf(caller.PersonId, cancellationToken).WaitAsync(cancellationToken);
        return members.Contains(current, StringComparer.Ordinal) ? members : [current, .. members];
    }

    public async Task<Instance[]> Running(CancellationToken cancellationToken = default)
    {
        var brain = BrainScope.CurrentId();
        var management = grains.GetGrain<IManagementGrain>(0);
        List<Instance> running = [];
        foreach (var type in await grains.GetGrain<IInstanceTypes>(brain).List().WaitAsync(cancellationToken))
        {
            var active = (await management.GetActiveGrains(GrainType.Create(type), cancellationToken).AsTask().WaitAsync(cancellationToken))
                .Select(id => id.Key.ToString()!).Where(key => BrainKey.BelongsTo(key, brain)).ToHashSet(StringComparer.Ordinal);
            if (active.Count == 0) { continue; }
            var known = await grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(brain, type)).List().WaitAsync(cancellationToken);
            running.AddRange(known.Where(instance => active.Contains(instance.Key)));
        }
        return [.. running];
    }

    public Task Retire(string type, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var brain = BrainScope.CurrentId();
        if (!BrainKey.BelongsTo(key, brain)) { throw new UnauthorizedAccessException($"'{key}' is not in the caller's brain."); }
        return grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(brain, type)).Retire(key).WaitAsync(cancellationToken);
    }

    public Task<Feature[]> Features(string brain, CancellationToken cancellationToken = default)
        => TypeTable(brain).Features().WaitAsync(cancellationToken);

    public Task LoadFeature(string brain, Feature feature, TypeEntry[] types, CancellationToken cancellationToken = default)
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

    private ITypeTable TypeTable(string brain)
    {
        if (!BrainScope.IsValidId(brain)) { throw new ArgumentException($"'{brain}' is not a brain id.", nameof(brain)); }
        return grains.GetGrain<ITypeTable>(brain);
    }

    // A contract's default grain type; a contract several grains implement has none and is refused.
    public string TypeOf(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (IntoChat.OS.Runtime.PlainNeuronContracts.TypeOf(contract) is { } plain) { return plain; }
        try { return grains.GetGrain(contract, "_").GetGrainId().Type.ToString()!; }
        catch (ArgumentException error) { throw new ArgumentException($"{contract.Name} has no single grain type: {error.Message}", nameof(contract), error); }
    }
}
