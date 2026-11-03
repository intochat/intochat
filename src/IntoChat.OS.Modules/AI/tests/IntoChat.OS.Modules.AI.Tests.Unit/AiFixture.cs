using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests
{
    // Unit tier: the plain AI neurons on TestOS only, the model neurons over a gated fake inference.
    public sealed class AiFixture : BothRuntimesFixture
    {
        public static TheoryData<NeuronRuntimeKind> Kinds => new(NeuronRuntimeKind.TestOS);

        public GatedInference Inference { get; } = new();

        protected override IReadOnlyList<Type> Modules { get; } = [typeof(AIModule)];

        protected override void ConfigureTestOS(IServiceCollection services)
            => services.AddSingleton<IntoChat.OS.Modules.AI.Models.IModelInference>(Inference);
    }
}

namespace DigitalBrain.Testing
{
    public enum NeuronRuntimeKind
    {
        TestOS,
        Silo,
    }

    public interface INeuronRuntime : IAsyncDisposable
    {
        NeuronRuntimeKind Kind { get; }
        BrainId Brain { get; }
        IServiceProvider Services { get; }
        NeuronId Id(string type, string key) => new(Brain, type, key);
        T Get<T>(NeuronId id) where T : class;
        Task Idle();
        Task Elapse(TimeSpan by, CancellationToken ct);
        Task<IReadOnlyList<T>> Signals<T>(NeuronId publisher, CancellationToken ct) where T : Signal;
        async Task<IReadOnlyList<T>> WaitForSignals<T>(NeuronId publisher, int count, TimeSpan within, CancellationToken ct) where T : Signal
        {
            var deadline = DateTime.UtcNow + within;
            while (true)
            {
                await Idle().ConfigureAwait(false);
                var signals = await Signals<T>(publisher, ct).ConfigureAwait(false);
                if (signals.Count >= count) { return signals; }
                if (DateTime.UtcNow > deadline) { throw new TimeoutException($"{publisher} journaled {signals.Count} {typeof(T).Name}, not {count}, within {within}."); }
                await Elapse(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
        }
        Task Restart(CancellationToken ct);
    }

    public abstract class BothRuntimesFixture : IAsyncLifetime
    {
        private readonly ConcurrentDictionary<NeuronRuntimeKind, Lazy<Task<INeuronRuntime>>> _shared = new();
        protected abstract IReadOnlyList<Type> Modules { get; }
        public virtual BrainId Brain { get; } = new("facts");
        protected virtual void ConfigureTestOS(IServiceCollection services) { }
        public ValueTask InitializeAsync() => ValueTask.CompletedTask;
        public Task<INeuronRuntime> Runtime(NeuronRuntimeKind kind)
            => _shared.GetOrAdd(kind, _ => new Lazy<Task<INeuronRuntime>>(() => Start(kind, CancellationToken.None))).Value;
        public Task<INeuronRuntime> Start(NeuronRuntimeKind kind, CancellationToken ct)
        {
            _ = kind;
            _ = ct;
            return Task.FromResult<INeuronRuntime>(TestOSRuntime.Start(Brain, Modules, TestOSServices()));
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var runtime in _shared.Values.Where(lazy => lazy.IsValueCreated).Select(lazy => lazy.Value))
            {
                if (runtime.IsCompletedSuccessfully) { await runtime.Result.DisposeAsync().ConfigureAwait(false); }
            }
            GC.SuppressFinalize(this);
        }
        private ServiceProvider? TestOSServices()
        {
            var services = new ServiceCollection();
            ConfigureTestOS(services);
            return services.Count == 0 ? null : services.BuildServiceProvider();
        }
        private sealed class TestOSRuntime(TestOS os, BrainId brain) : INeuronRuntime
        {
            public static TestOSRuntime Start(BrainId brain, IReadOnlyList<Type> modules, IServiceProvider? services = null)
            {
                var os = TestOS.Local(options => options.Services = services);
                os.Register(typeof(Signal).Assembly);
                foreach (var assembly in modules.Select(module => module.Assembly).Distinct()) { os.Register(assembly); }
                return new TestOSRuntime(os, brain);
            }
            public NeuronRuntimeKind Kind => NeuronRuntimeKind.TestOS;
            public BrainId Brain => brain;
            public IServiceProvider Services => os.Services;
            public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
            public Task Idle() => os.Idle();
            public async Task Elapse(TimeSpan by, CancellationToken ct)
            {
                os.Clock.Advance(by);
                await os.Idle().ConfigureAwait(false);
            }
            public Task<IReadOnlyList<T>> Signals<T>(NeuronId publisher, CancellationToken ct) where T : Signal
                => Task.FromResult<IReadOnlyList<T>>([.. os.Journal(publisher.Brain).Signals(publisher).Select(entry => entry.Signal).OfType<T>()]);
            public Task Restart(CancellationToken ct) => os.Restart();
            public ValueTask DisposeAsync() => os.DisposeAsync();
        }
    }
}
