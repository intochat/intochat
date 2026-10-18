using Microsoft.Extensions.Options;

namespace DigitalBrain.Framework;

// One partition of the instance directory: every instance of one grain type in one brain, keyed
// "{brain}/{type}". Not an INeuron, so no brain.Get, contract catalog or MCP surface can reach it.
[Alias("kernel.instance-directory")]
internal interface IInstanceDirectory : IGrainWithStringKey
{
    Task Register(string key, string? creator);
    Task Retire(string key);
    Task<Instance[]> List();
}

// The grain types a brain has instances of, keyed "{brain}".
[Alias("kernel.instance-types")]
internal interface IInstanceTypes : IGrainWithStringKey
{
    Task Add(string type);
    Task<string[]> List();
}

// The brain neuron publishes the kernel's instance signals, so they arrive with the brain as publisher.
[Alias("kernel.announcer")]
internal interface IKernelAnnouncer : IGrainWithStringKey
{
    [Orleans.Concurrency.OneWay] Task Announce(Signal signal);
}

internal static class InstanceDirectoryKey
{
    public static string Of(string brain, string type) => brain + "/" + type;
}

[GrainType("kernel.instances")]
internal sealed class InstanceDirectoryGrain(
    [PersistentState("instances", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<InstanceDirectoryGrain.State> store,
    IOptions<InstanceDirectoryOptions> options,
    TimeProvider time) : Grain, IInstanceDirectory
{
    [GenerateSerializer, Alias("kernel.instances.state")]
    public sealed class State
    {
        [Id(0)] public Instance[] Entries { get; set; } = [];
    }

    private string Brain => BrainKey.BrainOf(this.GetPrimaryKeyString());
    private string Type => this.GetPrimaryKeyString()[(Brain.Length + 1)..];

    public async Task Register(string key, string? creator)
    {
        if (!BrainKey.BelongsTo(key, Brain)) { throw new ArgumentException($"'{key}' is not in brain '{Brain}'.", nameof(key)); }
        if (store.State.Entries.Any(entry => entry.Key == key)) { return; }
        var first = store.State.Entries.Length == 0;
        var entries = new List<Instance>(store.State.Entries) { new(Type, key, time.GetUtcNow(), creator) };
        // Ledger 38: keep the directory bounded — drop the oldest entries that are not currently active once a
        // type exceeds the limit, so ephemeral neurons do not accumulate in the brain's core without bound.
        if (entries.Count > options.Value.Limit)
        {
            var active = await ActiveKeys();
            var over = entries.Count - options.Value.Limit;
            var kept = new List<Instance>(entries.Count);
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
        Announce(new InstanceCreated(Type, key));
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
        Announce(new InstanceRetired(Type, key));
    }

    public Task<Instance[]> List() => Task.FromResult(store.State.Entries);

    // One-way and unstamped: the brain neuron may be the very grain whose first write is being
    // registered, so awaiting it would deadlock; no caller travels because the kernel is the author.
    private void Announce(Signal signal)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        _ = GrainFactory.GetGrain<IKernelAnnouncer>(Brain).Announce(signal);
    }
}

[GrainType("kernel.types")]
internal sealed class InstanceTypesGrain(
    [PersistentState("types", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<InstanceTypesGrain.State> store) : Grain, IInstanceTypes
{
    [GenerateSerializer, Alias("kernel.types.state")]
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

internal static class TypeTableKey
{
    // A slash is never a brain id, so no brain's features can land under it.
    public const string NoBrain = "/";
}

// The types a brain knows, keyed "{brain}": the host's compiled layer, then the brain's own features.
// Unscoped discovery has no brain journal and must never activate persistent feature state.
[Alias("kernel.compiled-type-table")]
internal interface ICompiledTypeTable : IGrainWithStringKey
{
    Task<TypeEntry[]> List();
}

[GrainType("kernel.compiledtypes")]
internal sealed class CompiledTypeTableGrain(CompiledTypes compiled) : Grain, ICompiledTypeTable
{
    public Task<TypeEntry[]> List() => Task.FromResult(compiled.Addressed(GrainFactory));
}

[Alias("kernel.type-table")]
internal interface ITypeTable : IGrainWithStringKey
{
    Task<TypeEntry[]> List();
    Task<Feature[]> Features();
    Task Load(Feature feature, TypeEntry[] types);
    Task Unload(string name);
}

[GrainType("kernel.typetable")]
internal sealed class TypeTableGrain(
    CompiledTypes compiled,
    [PersistentState("features", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<TypeTableGrain.State> store) : Grain, ITypeTable
{
    // The loaded features, and the types each brought (a TypeEntry's Provider names its feature).
    [GenerateSerializer, Alias("kernel.typetable.state")]
    public sealed class State
    {
        [Id(0)] public TypeEntry[] Features { get; set; } = [];
        [Id(1)] public Feature[] Loaded { get; set; } = [];
    }

    // Two features may bring the same type; it is listed once, and stays while any of them is loaded.
    public Task<TypeEntry[]> List() => Task.FromResult<TypeEntry[]>([.. compiled.Addressed(GrainFactory), .. store.State.Features.DistinctBy(entry => entry.Name)]);

    public Task<Feature[]> Features() => Task.FromResult(store.State.Loaded);

    // Loading again replaces what the feature brought; a type no feature had before is announced.
    public async Task Load(Feature feature, TypeEntry[] types)
    {
        var provider = feature.Provider;
        var known = store.State.Features.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var stamped = types.Select(type => type with { Provider = provider }).ToArray();
        var loaded = store.State.Loaded.FirstOrDefault(each => each.Name == feature.Name);
        var same = loaded == feature
            && store.State.Features.Where(entry => entry.Provider == provider).Select(entry => entry.Name)
                .SequenceEqual(stamped.Select(entry => entry.Name), StringComparer.Ordinal);
        if (same) { return; }
        store.State.Loaded = [.. store.State.Loaded.Where(each => each.Name != feature.Name), feature];
        store.State.Features = [.. store.State.Features.Where(entry => entry.Provider != provider), .. stamped];
        await store.WriteStateAsync();
        if (this.GetPrimaryKeyString() is var brain && brain != TypeTableKey.NoBrain)
        {
            RequestContext.Remove(CallerContextStamper.RequestContextKey);
            foreach (var added in stamped.Where(type => !known.Contains(type.Name)))
            { _ = GrainFactory.GetGrain<IKernelAnnouncer>(brain).Announce(new TypeAdded(added.Name)); }
        }
    }

    public async Task Unload(string name)
    {
        if (store.State.Loaded.FirstOrDefault(each => each.Name == name) is not { } feature) { return; }
        store.State.Loaded = [.. store.State.Loaded.Where(each => each.Name != name)];
        store.State.Features = [.. store.State.Features.Where(entry => entry.Provider != feature.Provider)];
        await store.WriteStateAsync();
    }
}
