using System.Net;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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

    [Fact]
    public async Task SearchFallsBackToLexicalResultsWhenEmbedderThrows404()
    {
        var ct = TestContext.Current.CancellationToken;
        BrainType[] entries =
        [
            new("IntoChat.OS.Modules.Mcp.IMcpServer", "mcp.server", TypeKind.Neuron, "mcp",
                "Microsoft Learn documentation search over MCP.", ["Search(string query)"], []),
            new("IntoChat.OS.Modules.Apps.IFocus", "os.focus", TypeKind.Neuron, "apps",
                "Focus and highlight surfaces in the workspace.", ["Point(string target)"], []),
        ];
        var observer = new StubBrainObserver(entries);
        var logger = new CapturingLogger<NeuronTypeSearch>();
        using var embedder = new ThrowingEmbeddingGenerator(
            new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound));
        using var search = new NeuronTypeSearch(observer, [], embedder, logger);
        var surface = new OsNeuronSurface(null!, observer, search);

        var hits = await surface.Search("microsoft learn", ct);

        var hit = Assert.Single(hits);
        Assert.Equal("IntoChat.OS.Modules.Mcp.IMcpServer", hit.Type.Contract);
        Assert.True(hit.Score > 0);
        Assert.Contains(logger.Levels, static level => level == LogLevel.Warning);
    }

    private sealed class ThrowingEmbeddingGenerator(Exception error) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(error);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
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
