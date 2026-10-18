using DigitalBrain.Microsoft.Orleans;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Modules.Microsoft.Orleans.Tests.Integration;

public sealed class MemoryStoresFacts
{
    [Fact]
    public async Task AStoreKeepsItsETagAcrossProvidersAndRefusesAStaleWrite()
    {
        var stores = new MemoryStores();
        var serializer = new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<Serializer>();
        var first = new MemoryStoreGrainStorage(stores.Store("cluster"), serializer);
        var id = GrainId.Create("test", "key");
        var state = new GrainState<int> { State = 1 };
        await first.WriteStateAsync("s", id, state);
        var second = new MemoryStoreGrainStorage(stores.Store("cluster"), serializer);
        var read = new GrainState<int>();
        await second.ReadStateAsync("s", id, read);
        Assert.Equal(1, read.State);
        Assert.Equal(state.ETag, read.ETag);
        var stale = new GrainState<int> { State = 2, ETag = null };
        await Assert.ThrowsAsync<InconsistentStateException>(() => second.WriteStateAsync("s", id, stale));
    }
}
