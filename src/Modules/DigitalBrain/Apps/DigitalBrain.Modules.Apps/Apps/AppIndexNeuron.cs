using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("apps.index-state")]
public sealed class AppIndexState
{
    [Id(0)] public string[] Names { get; set; } = [];
}

// The brain's app names: an index, not domain state, so it publishes nothing — AppChanged
// from the app neuron is the fact.
[GrainType("apps.index")]
internal sealed class AppIndexNeuron(
    [PersistentState("apps.index", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<AppIndexState> store)
    : Neuron, IAppIndex
{
    public async Task Track(string name)
    {
        store.State ??= new();
        if (store.State.Names.Contains(name, StringComparer.Ordinal)) { return; }
        store.State.Names = [.. store.State.Names.Append(name).Order(StringComparer.Ordinal)];
        await store.WriteStateAsync();
    }

    public async Task Untrack(string name)
    {
        store.State ??= new();
        store.State.Names = [.. store.State.Names.Where(item => item != name)];
        await store.WriteStateAsync();
    }

    public Task<string[]> List() => Task.FromResult<string[]>([.. (store.State ??= new()).Names]);
}
