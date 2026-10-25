using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Testing.Neurons;

// The same fact on both runtimes (R4-05): a class fixture that starts the module under test on TestOS.Local() and on an
// in-process local silo (the factory behind SiloHost.Local, given the runtime BrainLog as its cluster store so stateful and
// publishing neurons work). Facts take a NeuronRuntimeKind theory parameter and ask Runtime(kind) for the shared brain, or
// Start(kind) for one of their own (restarts, own services). No os test kit is involved.
public abstract class BothRuntimesFixture : IAsyncLifetime
{
    private readonly ConcurrentDictionary<NeuronRuntimeKind, Lazy<Task<INeuronRuntime>>> _shared = new();

    // The DigitalBrain module classes ([Module] with a static ModuleManifest) whose plain neurons are under test.
    protected abstract IReadOnlyList<Type> Modules { get; }

    // The brain every runtime of this fixture hosts.
    public virtual BrainId Brain { get; } = new("facts");

    // Silo-only services, applied after the topology and runtime registrations (reminder periods, clocks).
    protected virtual void ConfigureSilo(IServiceCollection services) { }

    // Silo configuration values.
    protected virtual IReadOnlyDictionary<string, string?> SiloConfiguration { get; } = new Dictionary<string, string?>();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    // The fixture's shared runtime of kind; facts that share it use their own keys.
    public Task<INeuronRuntime> Runtime(NeuronRuntimeKind kind)
        => _shared.GetOrAdd(kind, value => new Lazy<Task<INeuronRuntime>>(() => Start(value, CancellationToken.None))).Value;

    // A runtime of kind that the caller disposes; home keeps the silo's state folder (null: a fresh temporary one).
    public async Task<INeuronRuntime> Start(NeuronRuntimeKind kind, CancellationToken ct, string? home = null)
    {
        var modules = Modules;
        if (modules.Count == 0) { throw new InvalidOperationException("A both-runtimes fixture needs at least one module."); }
        return kind switch
        {
            NeuronRuntimeKind.TestOS => TestOSRuntime.Start(Brain, modules),
            NeuronRuntimeKind.Silo => await SiloRuntime.Start(Brain, modules, ConfigureSilo, SiloConfiguration, home, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _shared.Values.Where(lazy => lazy.IsValueCreated).Select(lazy => lazy.Value))
        {
            if (runtime.IsCompletedSuccessfully) { await runtime.Result.DisposeAsync().ConfigureAwait(false); }
        }
        GC.SuppressFinalize(this);
    }

    internal static string AliasOf(Type signal) => signal.GetCustomAttribute<SignalAttribute>(inherit: false)?.Alias ?? signal.FullName!;

    private sealed class TestOSRuntime(TestOS os, BrainId brain) : INeuronRuntime
    {
        public static TestOSRuntime Start(BrainId brain, IReadOnlyList<Type> modules)
        {
            var os = TestOS.Local();
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

    private sealed class SiloRuntime(IInProcessHost host, BrainId brain, IReadOnlyList<Type> modules) : INeuronRuntime
    {
        public static async Task<SiloRuntime> Start(BrainId brain, IReadOnlyList<Type> modules, Action<IServiceCollection> configure,
            IReadOnlyDictionary<string, string?> configuration, string? home, CancellationToken ct)
        {
            Assembly[] assemblies = [.. modules.Select(module => module.Assembly).Distinct()];
            var factory = new LocalSiloHostFactory(new BrainStorageRegistration(assemblies), configure);
            var options = new InProcessHostOptions { Home = home, Modules = modules, Configuration = configuration };
            var host = await factory.Start(options, ct).ConfigureAwait(false);
            return new SiloRuntime(host, brain, modules);
        }

        public NeuronRuntimeKind Kind => NeuronRuntimeKind.Silo;

        public BrainId Brain => brain;

        public IServiceProvider Services => host.Services;

        public T Get<T>(NeuronId id) where T : class => NeuronProxy.Create<T>(id, new HostSink(this));

        public Task Idle() => Task.CompletedTask;

        public Task Elapse(TimeSpan by, CancellationToken ct) => Task.Delay(by, ct);

        public async Task<IReadOnlyList<T>> Signals<T>(NeuronId publisher, CancellationToken ct) where T : Signal
        {
            var journal = host.Services.GetRequiredService<IBrainJournalWriter>();
            await journal.Claim(publisher.Brain.Value, ct).ConfigureAwait(false);
            var page = await journal.ReadSince(publisher.Brain.Value, publisher.ToString(), 0, 10_000, ct).ConfigureAwait(false);
            var alias = AliasOf(typeof(T));
            return [.. page.Signals.Where(entry => entry.Type == alias).Select(entry => JsonSerializer.Deserialize<T>(entry.Json.Span, RuntimeProtocol.Json)!)];
        }

        public Task Restart(CancellationToken ct) => host.Restart(ct);

        public ValueTask DisposeAsync() => host.DisposeAsync();

        // The host grain of a hosted class, by the runtime's policy: stateless, reentrant or brain-affine.
        internal INeuronHost Host(NeuronId target)
        {
            var neuronClass = modules.Select(module => module.Assembly).Distinct().SelectMany(assembly => assembly.GetTypes())
                .FirstOrDefault(type => type is { IsAbstract: false, IsClass: true } && typeof(Neuron).IsAssignableFrom(type)
                    && type.GetCustomAttribute<NeuronAttribute>(inherit: false)?.Type == target.Type)
                ?? throw new InvalidOperationException($"No module under test hosts neuron type '{target.Type}'.");
            var grainType = neuronClass.IsDefined(typeof(StatelessNeuronAttribute), inherit: false) ? "neuron.host.stateless"
                : neuronClass.IsDefined(typeof(ReentrantAttribute), inherit: true) ? "neuron.host.reentrant" : "neuron.host";
            return host.Services.GetRequiredService<IGrainFactory>().GetGrain<INeuronHost>(GrainId.Create(grainType, target.ToString()));
        }
    }

    // A client-side sink: calls carry no neuron caller, as a test edge does.
    private sealed class HostSink(SiloRuntime runtime) : IInvocationSink
    {
        public Task<JsonElement?> Invoke(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken)
            => runtime.Host(target).Invoke(call, cancellationToken);

        public Task Post(NeuronId target, RuntimeInvocation call) => runtime.Host(target).Post(call);

        public async IAsyncEnumerable<JsonElement> Stream(NeuronId target, RuntimeInvocation call, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in runtime.Host(target).Stream(call, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) { yield return item; }
        }
    }
}
