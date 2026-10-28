using System.Reflection;
using System.Runtime.CompilerServices;
using DigitalBrain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace DigitalBrain.Runtime.Orleans;

/// <summary>
/// The kernel's read-only facts about the caller's brain: which neurons exist, which types are compiled, which brains the caller is in.
/// The brain is the stamped caller's. Step 3 adds synapse listing and delivery subscription.
/// </summary>
public interface IBrainObserver
{
    /// <summary>Instances of one neuron contract in the caller's brain.</summary>
    IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class;

    /// <summary>Instances of one grain type, or of every type when <paramref name="type"/> is null.</summary>
    IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default);

    /// <summary>The compiled modules' neurons, signals and connectors, then the brain's own features.</summary>
    Task<BrainType[]> Types(CancellationToken cancellationToken = default);

    /// <summary>The brains the stamped caller is a member of, the current one included.</summary>
    Task<string[]> Brains(CancellationToken cancellationToken = default);
}

/// <summary>The stamped caller an <see cref="IBrainObserver"/> reads. The host registers it; a runtime-only silo has none.</summary>
internal interface IObservedCaller
{
    /// <summary>The brain of the current call.</summary>
    string BrainId { get; }

    /// <summary>The person of the current call.</summary>
    string PersonId { get; }

    /// <summary>The brains <paramref name="personId"/> is a member of.</summary>
    Task<string[]> BrainsOf(string personId, CancellationToken cancellationToken);
}

/// <summary>Reads the instance directory and type tables through their grains.</summary>
internal sealed class ClusterBrainObserver(IGrainFactory grains, IServiceProvider services) : IBrainObserver
{
    public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken = default) where T : class
        => Instances(TypeOf(typeof(T)), cancellationToken);

    public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
        => InstancesIn(Caller().BrainId, type, cancellationToken);

    public async IAsyncEnumerable<BrainInstance> InstancesIn(string brain, string? type, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        string[] types = type is null
            ? await grains.GetGrain<IInstanceTypes>(brain).List().WaitAsync(cancellationToken).ConfigureAwait(false)
            : [type];
        foreach (var each in types)
        {
            foreach (var instance in await grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(brain, each)).List().WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return instance;
            }
        }
    }

    public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
        => grains.GetGrain<ICompiledTypeTable>(TypeTableKey.NoBrain).List().WaitAsync(cancellationToken);

    public async Task<string[]> Brains(CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        var members = await caller.BrainsOf(caller.PersonId, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        return members.Contains(caller.BrainId, StringComparer.Ordinal) ? members : [caller.BrainId, .. members];
    }

    private IObservedCaller Caller()
        => services.GetService<IObservedCaller>()
            ?? throw new InvalidOperationException("No caller is stamped and no IObservedCaller is registered.");

    private string TypeOf(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type is { } plain) { return plain; }
        try { return grains.GetGrain(contract, "_").GetGrainId().Type.ToString()!; }
        catch (ArgumentException error)
        {
            throw new ArgumentException($"{contract.Name} has no single grain type: {error.Message}", nameof(contract), error);
        }
    }
}

[Alias("kernel.instance-directory")]
internal interface IInstanceDirectory : IGrainWithStringKey
{
    Task Register(string key, string? creator);
    Task Retire(string key);
    Task<BrainInstance[]> List();
}

[Alias("kernel.instance-types")]
internal interface IInstanceTypes : IGrainWithStringKey
{
    Task Add(string type);
    Task<string[]> List();
}

internal static class InstanceDirectoryKey
{
    public static string Of(string brain, string type) => brain + "/" + type;
}

internal static class TypeTableKey
{
    public const string NoBrain = "/";
}

[Alias("kernel.compiled-type-table")]
internal interface ICompiledTypeTable : IGrainWithStringKey
{
    Task<BrainType[]> List();
    Task<string?> HostType(string type);
}
