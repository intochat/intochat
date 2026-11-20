using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;

namespace IntoChat.OS.Authority;

public sealed class ScreenWatchPlainIdFacts
{
    [Fact]
    public async Task PlainIdResolvesCompiledScreenNeuronTypeAndViewChangedIsInCompiledTypes()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiled = new CompiledTypes([typeof(IScreen).Assembly]);
        var surface = new OsNeuronSurface(null!, new StubBrainObserver(compiled.Entries), null!);

        var engineerId = await surface.PlainId(typeof(IScreen).FullName!, "owner/app.engineer/default", ct);
        Assert.Equal(new NeuronId(new BrainId("owner"), "app.engineer", "default"), engineerId);

        var creatorId = await surface.PlainId(typeof(IScreen).FullName!, "owner/app.creator/default", ct);
        Assert.Equal(new NeuronId(new BrainId("owner"), "app.creator", "default"), creatorId);

        var tasksKey = await surface.Get(typeof(IScreen).FullName!, "owner/app.tasks/default", ct);
        Assert.Equal("owner/app.tasks/default", tasksKey);

        Assert.Contains(
            compiled.Entries,
            entry => entry.Kind == TypeKind.Signal && entry.Name == typeof(ViewChanged).FullName);
    }

    private sealed class StubBrainObserver(BrainType[] types) : IBrainObserver
    {
        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class
            => AsyncEnumerable.Empty<BrainInstance>();

        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<BrainInstance>();

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
            => Task.FromResult(types);

        public Task<string[]> Brains(CancellationToken cancellationToken = default)
            => Task.FromResult<string[]>([]);

        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default)
            => Task.FromResult<ObservedWire[]>([]);

        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<SignalDelivery>();

        public Task<JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);
    }
}
