using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Contracts.Configuration;
using IntoChatOSNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Framework;
using DigitalBrain.State;
using DigitalBrain.Runtime.Orleans.Hosting;

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
        // Ledger 38: keep the directory bounded — drop the oldest entries that are not currently active once a
        // type exceeds the limit, so ephemeral neurons do not accumulate in the brain's core without bound.
        if (entries.Count > options.Value.Limit)
        {
            var active = await ActiveKeys();
            var over = entries.Count - options.Value.Limit;
            var kept = new List<BrainInstance>(entries.Count);
            foreach (var entry in entries.OrderBy(entry => entry.Created))
            {
                if (over > 0 && !active.Contains(entry.Key)) { over--; continue; }
                kept.Add(entry);
            }
            entries = kept;
        }
        store.State.Entries = [.. entries];
        await store.WriteStateAsync();
        if (first) { await GrainFactory.GetGrain<IInstanceTypes>(Brain).Add(Type); }
        Announce(brain => brain.InstanceCreated(Type, key));
    }

    // The live instances of this directory's type in its brain: compaction never drops these.
    private async Task<HashSet<string>> ActiveKeys()
    {
        var active = await GrainFactory.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create(Type));
        return [.. active.Select(id => id.Key.ToString()!).Where(key => BrainKey.BelongsTo(key, Brain))];
    }

    public async Task Retire(string key)
    {
        if (!store.State.Entries.Any(entry => entry.Key == key)) { return; }
        store.State.Entries = [.. store.State.Entries.Where(entry => entry.Key != key)];
        await store.WriteStateAsync();
        Announce(brain => brain.InstanceRetired(Type, key));
    }

    public Task<BrainInstance[]> List() => Task.FromResult(store.State.Entries);

    // One-way and unstamped: the brain neuron may be the very grain whose first write is being
    // registered, so awaiting it would deadlock; no caller travels because the kernel is the author.
    private void Announce(Func<IBrainAnnouncer, Task> announce)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        _ = announce(SiloNeurons.Get<IBrainAnnouncer>(ServiceProvider, IBrain.Key(Brain)));
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
    public Task<BrainType[]> List() => Task.FromResult(compiled.Addressed(contract => Kernel.StillGrainType(GrainFactory, contract)));

    // A contracts-only client asks the composed kernel for the host policy, then calls that host directly. Resolving metadata
    // never forwards an invocation or turns an external caller into a trusted call from a kernel grain.
    public Task<string?> HostType(string type)
        => Task.FromResult(compiled.Entries.Any(entry =>
            (entry.Kind == DigitalBrain.TypeKind.Neuron && IntoChat.OS.Runtime.PlainNeuronContracts.TypeOf(compiled.Contract(entry.Name)) == type)
            || (entry.Kind == DigitalBrain.TypeKind.Connector && entry.Alias == type))
            ? KernelInvocationSink.HostTypeOfLoaded(type) : null);
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
    public Task<BrainType[]> List() => Task.FromResult<BrainType[]>([.. compiled.Addressed(contract => Kernel.StillGrainType(GrainFactory, contract)), .. store.State.Features.DistinctBy(entry => entry.Name)]);

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
        if (this.GetPrimaryKeyString() is var brain && brain != TypeTableKey.NoBrain)
        {
            RequestContext.Remove(CallerContextStamper.RequestContextKey);
            var announcer = SiloNeurons.Get<IBrainAnnouncer>(ServiceProvider, IBrain.Key(brain));
            foreach (var added in stamped.Where(type => !known.Contains(type.Name)))
            { _ = announcer.TypeAdded(added.Name); }
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
