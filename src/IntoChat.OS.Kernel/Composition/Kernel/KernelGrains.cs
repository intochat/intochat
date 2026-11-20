using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Brain;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.State;
using IntoChat.OS.Framework;
using IntoChat.OS.Kernel.Composition.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChatOSNames = IntoChat.OS.Host.Configuration.IntoChatOSNames;
using CompositionKernel = IntoChat.OS.Kernel.Composition.Kernel.OsKernel;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Callers;

namespace IntoChat.OS.Kernel.Composition;

// The kernel's grains (06-02, R4-12): the instance directory, the instance types and the type tables. Their
// contracts stay in the Framework, which the neurons register through, until 06-08.
[GrainType("kernel.instances")]
internal sealed class InstanceDirectoryGrain(
    [PersistentState("instances", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<InstanceDirectoryGrain.State> store,
    IOptions<InstanceDirectoryOptions> options,
    TimeProvider time) : Grain, IInstanceDirectory
{
    [GenerateSerializer, Alias("kernel.instances.state")]
    [ExportableState]
    public sealed class State
    {
        [Id(0)] public BrainInstance[] Entries { get; set; } = [];
    }

    private string Brain => BrainKey.BrainOf(this.GetPrimaryKeyString());
    private string Type => this.GetPrimaryKeyString()[(Brain.Length + 1)..];

    public async Task Register(string key, string? creator)
    {
        if (!BrainKey.BelongsTo(key, Brain)) { throw new ArgumentException($"'{key}' is not in brain '{Brain}'.", nameof(key)); }
        if (store.State.Entries.Any(entry => entry.Key == key)) { return; }
        var first = store.State.Entries.Length == 0;
        var entries = new List<BrainInstance>(store.State.Entries) { new(Type, key, time.GetUtcNow(), creator) };
        // Keep the directory bounded — drop the oldest entries once a type exceeds the limit.
        if (entries.Count > options.Value.Limit)
        {
            var over = entries.Count - options.Value.Limit;
            entries = entries.OrderBy(entry => entry.Created).Skip(over).ToList();
        }
        store.State.Entries = [.. entries];
        await store.WriteStateAsync();
        if (first) { await GrainFactory.GetGrain<IInstanceTypes>(Brain).Add(Type); }
        Announce(brain => brain.InstanceCreated(Type, key));
    }

    public async Task Retire(string key)
    {
        if (!store.State.Entries.Any(entry => entry.Key == key)) { return; }
        store.State.Entries = [.. store.State.Entries.Where(entry => entry.Key != key)];
        await store.WriteStateAsync();
        Announce(brain => brain.InstanceRetired(Type, key));
    }

    public Task<BrainInstance[]> List() => Task.FromResult(store.State.Entries);

    // One-way: the brain neuron may be the very grain whose first write is being registered, so awaiting it would
    // deadlock. The kernel is the author and Announce is kernel-only, so the call carries the kernel caller.
    private void Announce(Func<IBrainAnnouncer, Task> announce)
    {
        if (ServiceProvider is not { } services) { return; }
        KernelAnnouncement.Send(services, new BrainId(Brain), announce);
    }
}

[GrainType("kernel.types")]
internal sealed class InstanceTypesGrain(
    [PersistentState("types", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<InstanceTypesGrain.State> store) : Grain, IInstanceTypes
{
    [GenerateSerializer, Alias("kernel.types.state")]
    [ExportableState]
    public sealed class State
    {
        [Id(0)] public string[] Types { get; set; } = [];
    }

    public async Task Add(string type)
    {
        if (store.State.Types.Contains(type, StringComparer.Ordinal)) { return; }
        store.State.Types = [.. store.State.Types, type];
        await store.WriteStateAsync();
    }

    public Task<string[]> List() => Task.FromResult(store.State.Types);
}

[GrainType("kernel.compiledtypes")]
internal sealed class CompiledTypeTableGrain(CompiledTypes compiled) : Grain, ICompiledTypeTable, DigitalBrain.Runtime.Orleans.Hosting.INeuronHostCatalog
{
    public Task<BrainType[]> List() => Task.FromResult(compiled.Addressed(contract => CompositionKernel.StillGrainType(GrainFactory, contract)));

    // A contracts-only client asks the composed kernel for the host policy, then calls that host directly. Resolving metadata
    // never forwards an invocation or turns an external caller into a trusted call from a kernel grain.
    public Task<string?> HostType(string type)
        => Task.FromResult(compiled.Entries.Any(entry =>
            (entry.Kind == TypeKind.Neuron && DigitalBrain.Contracts.PlainNeuronContracts.TypeOf(compiled.Contract(entry.Name)) == type)
            || (entry.Kind == TypeKind.Connector && entry.Alias == type))
            ? KernelInvocationSink.HostTypeOfLoaded(type) : null);
}

[Alias("kernel.type-table")]
internal interface ITypeTable : IGrainWithStringKey
{
    Task<BrainType[]> List();
    Task<Feature[]> Features();
    Task Load(Feature feature, BrainType[] types);
    Task Unload(string name);
}

[GrainType("kernel.typetable")]
internal sealed class TypeTableGrain(
    CompiledTypes compiled,
    [PersistentState("features", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<TypeTableGrain.State> store) : Grain, ITypeTable
{
    // The loaded features, and the types each brought (a BrainType's Origin names its feature).
    [GenerateSerializer, Alias("kernel.typetable.state")]
    [ExportableState]
    public sealed class State
    {
        [Id(0)] public BrainType[] Features { get; set; } = [];
        [Id(1)] public Feature[] Loaded { get; set; } = [];
    }

    // Two features may bring the same type; it is listed once, and stays while any of them is loaded.
    public Task<BrainType[]> List() => Task.FromResult<BrainType[]>([.. compiled.Addressed(contract => CompositionKernel.StillGrainType(GrainFactory, contract)), .. store.State.Features.DistinctBy(entry => entry.Name)]);

    public Task<Feature[]> Features() => Task.FromResult(store.State.Loaded);

    // Loading again replaces what the feature brought; a type no feature had before is announced.
    public async Task Load(Feature feature, BrainType[] types)
    {
        var provider = feature.Provider;
        var known = store.State.Features.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var stamped = types.Select(type => type with { Origin = provider }).ToArray();
        var loaded = store.State.Loaded.FirstOrDefault(each => each.Name == feature.Name);
        var same = loaded == feature
            && store.State.Features.Where(entry => entry.Origin == provider).Select(entry => entry.Name)
                .SequenceEqual(stamped.Select(entry => entry.Name), StringComparer.Ordinal);
        if (same) { return; }
        store.State.Loaded = [.. store.State.Loaded.Where(each => each.Name != feature.Name), feature];
        store.State.Features = [.. store.State.Features.Where(entry => entry.Origin != provider), .. stamped];
        await store.WriteStateAsync();
        if (this.GetPrimaryKeyString() is var brain && brain != TypeTableKey.NoBrain && ServiceProvider is { } services)
        {
            var added = stamped.Where(type => !known.Contains(type.Name)).Select(type => type.Name).ToArray();
            KernelAnnouncement.Send(services, new BrainId(brain), announcer =>
            {
                foreach (var name in added) { _ = announcer.TypeAdded(name); }
                return Task.CompletedTask;
            });
        }
    }

    public async Task Unload(string name)
    {
        if (store.State.Loaded.FirstOrDefault(each => each.Name == name) is not { } feature) { return; }
        store.State.Loaded = [.. store.State.Loaded.Where(each => each.Name != name)];
        store.State.Features = [.. store.State.Features.Where(entry => entry.Origin != feature.Provider)];
        await store.WriteStateAsync();
    }
}

// Brain announcements are kernel-only (BrainNeuron.Announce). The kernel grains send them one-way with the kernel caller,
// entered through the platform's one door, never awaited.
internal static class KernelAnnouncement
{
    public static void Send(IServiceProvider services, BrainId brain, Func<IBrainAnnouncer, Task> announce)
        => Send(services.GetRequiredService<DigitalBrain.Access.EdgeAuthority>(), brain,
            () => SiloNeurons.Get<IBrainAnnouncer>(services, IBrain.Key(brain.Value)), announce);

    internal static void Send(DigitalBrain.Access.EdgeAuthority edge, BrainId brain, Func<IBrainAnnouncer> announcer, Func<IBrainAnnouncer, Task> announce)
    {
        ArgumentNullException.ThrowIfNull(edge);
        _ = edge.AsKernel(brain, () =>
        {
            _ = announce(announcer());
            return Task.FromResult(true);
        });
    }
}
