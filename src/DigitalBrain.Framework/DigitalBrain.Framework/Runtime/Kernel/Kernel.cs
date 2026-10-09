using System.Runtime.CompilerServices;

namespace DigitalBrain.Framework;

// Reads the directory of the stamped caller's brain; works on a silo and on a cluster client alike.
public sealed class Kernel(IGrainFactory grains) : IKernel
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

    public async Task<Instance[]> Running(CancellationToken cancellationToken = default)
    {
        var brain = BrainScope.CurrentId();
        var management = grains.GetGrain<IManagementGrain>(0);
        List<Instance> running = [];
        foreach (var type in await grains.GetGrain<IInstanceTypes>(brain).List().WaitAsync(cancellationToken))
        {
            var active = (await management.GetActiveGrains(GrainType.Create(type)).AsTask().WaitAsync(cancellationToken))
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

    // A contract's default grain type; a contract several grains implement has none and is refused.
    public string TypeOf(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        try { return grains.GetGrain(contract, "_").GetGrainId().Type.ToString()!; }
        catch (ArgumentException error) { throw new ArgumentException($"{contract.Name} has no single grain type: {error.Message}", nameof(contract), error); }
    }
}
