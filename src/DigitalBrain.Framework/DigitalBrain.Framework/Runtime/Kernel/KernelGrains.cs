namespace DigitalBrain.Framework;

// One partition of the instance directory: every instance of one grain type in one brain, keyed
// "{brain}/{type}". Not an INeuron, so no brain.Get, contract catalog or MCP surface can reach it.
internal interface IInstanceDirectory : IGrainWithStringKey
{
    Task Register(string key, string? creator);
    Task Retire(string key);
    Task<Instance[]> List();
}

// The grain types a brain has instances of, keyed "{brain}".
internal interface IInstanceTypes : IGrainWithStringKey
{
    Task Add(string type);
    Task<string[]> List();
}

// The brain neuron publishes the kernel's instance signals, so they arrive with the brain as publisher.
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
    [PersistentState("instances", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<InstanceDirectoryGrain.State> store,
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
        store.State.Entries = [.. store.State.Entries, new Instance(Type, key, time.GetUtcNow(), creator)];
        await store.WriteStateAsync();
        if (first) { await GrainFactory.GetGrain<IInstanceTypes>(Brain).Add(Type); }
        Announce(new InstanceCreated(Type, key));
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
    [PersistentState("types", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<InstanceTypesGrain.State> store) : Grain, IInstanceTypes
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
