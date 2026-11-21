using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Host;

public sealed class NeuronSurfaceFacts
{
    [NeuronAlias("test.overloaded-surface"), DefaultNeuronType("test.overloaded-surface")]
    public interface IOverloadedSurfaceContract
    {
        Task<string> Lookup(string query);
        Task<string> Lookup(string query, int limit, bool includeArchived = false);
    }

    [Fact]
    public async Task CallingPointWithWrongArityNamesTheExpectedParameters()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiled = new CompiledTypes([typeof(IFocus).Assembly]);
        var surface = new OsNeuronSurface(null!, new StubBrainObserver(compiled.Entries), null!);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            surface.Call(typeof(IFocus).FullName!, "owner/ino/focus", "Point", """["window:settings", "Info"]""", ct));

        Assert.Contains("Point(target, tone, reason[, action])", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallingAnOverloadedMethodWithWrongArityListsEveryOverloadSignature()
    {
        var ct = TestContext.Current.CancellationToken;
        BrainType[] entries =
        [
            new(typeof(IOverloadedSurfaceContract).FullName!, "test.overloaded-surface", TypeKind.Neuron, "test", "", [], []),
        ];
        var surface = new OsNeuronSurface(null!, new StubBrainObserver(entries), null!);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            surface.Call(typeof(IOverloadedSurfaceContract).FullName!, "owner/test/overloaded", "Lookup", "[]", ct));

        Assert.Contains("Lookup(query)", error.Message, StringComparison.Ordinal);
        Assert.Contains("Lookup(query, limit[, includeArchived])", error.Message, StringComparison.Ordinal);
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
