using System.Net;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI.Controls;
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
        var surface = new NeuronSurface(observer: new StubBrainObserver(compiled.Entries));

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
        var surface = new NeuronSurface(observer: new StubBrainObserver(entries));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            surface.Call(typeof(IOverloadedSurfaceContract).FullName!, "owner/test/overloaded", "Lookup", "[]", ct));

        Assert.Contains("Lookup(query)", error.Message, StringComparison.Ordinal);
        Assert.Contains("Lookup(query, limit[, includeArchived])", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenAndControlContractsResolveAndDispatchThroughUnifiedNeuronSurface()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiled = new CompiledTypes(
            [typeof(IScreen).Assembly, typeof(IListBoxControl).Assembly],
            screenAssemblies: [typeof(GalleryApp).Assembly]);

        Assert.Same(typeof(IScreen), compiled.Contract("app.gallery"));
        Assert.Same(typeof(IListBoxControl), compiled.Contract(typeof(IListBoxControl).FullName!));
        Assert.Contains(compiled.Entries, entry => entry.Name == "app.gallery" && entry.Implements.Contains(typeof(IScreen).FullName));
        Assert.Contains(compiled.Entries, entry =>
            entry.Name == typeof(IListBoxControl).FullName
            && entry.Implements.Contains(typeof(IScreen).FullName)
            && entry.Shape.Any(m => m.Contains(" Read(", StringComparison.Ordinal)));

        var proxies = new CapturingScreenProxies();
        var surface = new NeuronSurface(
            proxies: proxies,
            observer: new StubBrainObserver(compiled.Entries));

        // 1. Concrete screen app addressed by the alias + key that neurons_list emits ("app.gallery", "owner/default").
        Assert.Equal("owner/app.gallery/default", await surface.Get("app.gallery", "owner/default", ct));
        Assert.Equal(new NeuronId(new BrainId("owner"), "app.gallery", "default"), await surface.PlainId("app.gallery", "owner/default", ct));
        var galleryRead = await surface.Call("app.gallery", "owner/default", nameof(IScreen.Read), "{}", ct);
        Assert.NotNull(galleryRead);
        Assert.Equal(new NeuronId(new BrainId("owner"), "app.gallery", "default"), proxies.LastId);

        // 2. Concrete screen app addressed through IScreen directly ("DigitalBrain.IScreen", "owner/app.gallery/default").
        var screenRead = await surface.Call(typeof(IScreen).FullName!, "owner/app.gallery/default", nameof(IScreen.Read), "{}", ct);
        Assert.NotNull(screenRead);
        Assert.Equal(new NeuronId(new BrainId("owner"), "app.gallery", "default"), proxies.LastId);

        // 3. Control neuron contract (IListBoxControl : IScreen, IControl<ListBoxProps>) addressed by alias ("control.listBox") or full contract name.
        Assert.Equal("owner/control.listBox/panel-1", await surface.Get("control.listBox", "owner/panel-1", ct));
        Assert.Equal(new NeuronId(new BrainId("owner"), "control.listBox", "panel-1"), await surface.PlainId("control.listBox", "owner/panel-1", ct));
        var controlRead = await surface.Call("control.listBox", "owner/panel-1", nameof(IScreen.Read), "{}", ct);
        Assert.NotNull(controlRead);
        Assert.Equal(new NeuronId(new BrainId("owner"), "control.listBox", "panel-1"), proxies.LastId);

        var controlByFullName = await surface.Call(typeof(IListBoxControl).FullName!, "owner/panel-2", nameof(IScreen.Read), "{}", ct);
        Assert.NotNull(controlByFullName);
        Assert.Equal(new NeuronId(new BrainId("owner"), "control.listBox", "panel-2"), proxies.LastId);
    }

    [Fact]
    public async Task SearchFallsBackToLexicalResultsWhenEmbedderThrows404()
    {
        var ct = TestContext.Current.CancellationToken;
        BrainType[] entries =
        [
            new("IntoChat.OS.Modules.Mcp.IMcpToolConnection", "mcp", TypeKind.Neuron, "mcp",
                "Microsoft Learn documentation search over MCP.", ["Search(string query)"], []),
            new("IntoChat.OS.Modules.Apps.IFocus", "os.focus", TypeKind.Neuron, "apps",
                "Focus and highlight surfaces in the workspace.", ["Point(string target)"], []),
        ];
        var observer = new StubBrainObserver(entries);
        var logger = new CapturingLogger<NeuronTypeSearch>();
        using var embedder = new ThrowingEmbeddingGenerator(
            new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound));
        using var search = new NeuronTypeSearch(observer, [], embedder, logger);
        var surface = new NeuronSurface(observer: observer, search: search);

        var hits = await surface.Search("microsoft learn", ct);

        var hit = Assert.Single(hits);
        Assert.Equal("IntoChat.OS.Modules.Mcp.IMcpToolConnection", hit.Type.Contract);
        Assert.True(hit.Score > 0);
        Assert.Contains(logger.Levels, static level => level == LogLevel.Warning);
    }

    private sealed class CapturingScreenProxies : INeuronProxies, IListBoxControl
    {
        private static readonly JsonElement EmptyView = JsonDocument.Parse("{}").RootElement.Clone();

        public NeuronId LastId { get; private set; }

        public T Get<T>(NeuronId id) where T : class
        {
            LastId = id;
            return (T)(object)this;
        }

        public Task<PublishedView?> Open()
            => Task.FromResult<PublishedView?>(new PublishedView("stub", 1, EmptyView));

        public Task<PublishedView?> Read(string? alias = null)
            => Task.FromResult<PublishedView?>(new PublishedView(alias ?? "stub", 1, EmptyView));

        public Task Request(string signal, JsonElement? args = null)
            => Task.CompletedTask;

        public Task Dispatch(Command command)
            => Task.CompletedTask;

        public Task<PublishedView?> ReadChild(string path)
            => Task.FromResult<PublishedView?>(null);
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
