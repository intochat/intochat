using DigitalBrain.Modules;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DigitalBrain.Testing;

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

    // TestOS-only services (06-05): what neuron constructors and Context.Services resolve on TestOS (TestOSOptions.Services);
    // none by default. A fact that needs a service on both kinds registers it here and in ConfigureSilo.
    protected virtual void ConfigureTestOS(IServiceCollection services) { }

    // Silo configuration values.
    protected virtual IReadOnlyDictionary<string, string?> SiloConfiguration { get; } = new Dictionary<string, string?>();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    // The fixture's shared runtime of kind; facts that share it use their own keys.
    public Task<INeuronRuntime> Runtime(NeuronRuntimeKind kind)
        => _shared.GetOrAdd(kind, value => new Lazy<Task<INeuronRuntime>>(() => Start(value, CancellationToken.None))).Value;

    public async Task<AppSession<TScreen>> Open<TScreen>(NeuronRuntimeKind kind, string? key = null, CancellationToken ct = default)
        where TScreen : class
    {
        var runtime = await Runtime(kind).ConfigureAwait(false);
        return await AppSession.OpenAsync<TScreen>(runtime, key, ct).ConfigureAwait(false);
    }

    [Module("both-runtimes-core")]
    private sealed class CoreModule : DigitalBrain.Modules.Module
    {
        public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    // A runtime of kind that the caller disposes; home keeps the silo's state folder (null: a fresh temporary one) and
    // siloServices runs after ConfigureSilo (fault injection, controls).
    public async Task<INeuronRuntime> Start(NeuronRuntimeKind kind, CancellationToken ct, string? home = null, Action<IServiceCollection>? siloServices = null)
    {
        var modules = Modules;
        if (modules.Count == 0)
        {
            modules = [typeof(CoreModule)];
        }
        return kind switch
        {
            NeuronRuntimeKind.TestOS => TestOSRuntime.Start(Brain, modules, ConfigureTestOS),
            NeuronRuntimeKind.Silo => await SiloRuntime.Start(Brain, modules, services => { ConfigureSilo(services); siloServices?.Invoke(services); },
                SiloConfiguration, home, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var runtime in _shared.Values.Where(lazy => lazy.IsValueCreated).Select(lazy => lazy.Value))
            {
                if (runtime.IsCompletedSuccessfully) { await runtime.Result.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Runs after shared runtimes have stopped, even if runtime disposal fails.</summary>
    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    internal static string AliasOf(Type signal) => signal.GetCustomAttribute<SignalAttribute>(inherit: false)?.Alias ?? signal.FullName!;

    private sealed class TestOSRuntime(TestOS os, BrainId brain, ServiceProvider serviceRoot) : INeuronRuntime
    {
        public static TestOSRuntime Start(BrainId brain, IReadOnlyList<Type> modules, Action<IServiceCollection> configure)
        {
            TestOS? os = null;
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton(_ => os!.Edge);
            services.AddSingleton(_ => os!.Callers);
            services.AddSingleton(_ => os!.Proxies);
            configure(services);
            services.TryAddSingleton<TimeProvider>(_ => os!.Clock);
            var serviceRoot = services.BuildServiceProvider();
            os = TestOS.Local(options => options.Services = serviceRoot);
            os.Register(typeof(Signal).Assembly);
            foreach (var assembly in modules.Select(module => module.Assembly).Distinct()) { os.Register(assembly); }
            return new TestOSRuntime(os, brain, serviceRoot);
        }

        public NeuronRuntimeKind Kind => NeuronRuntimeKind.TestOS;

        public BrainId Brain => brain;

        public IServiceProvider Services => os.Services;

        public DigitalBrain.Access.EdgeAuthority Edge => os.Edge;

        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);

        public Task Idle() => os.Idle();

        public async Task Elapse(TimeSpan by, CancellationToken ct)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
            var end = os.Clock.GetUtcNow() + by;
            while (os.Clock.Step(end))
            {
                ct.ThrowIfCancellationRequested();
                await os.Idle().ConfigureAwait(false);
            }
            await os.Idle().ConfigureAwait(false);
        }

        public Task<IReadOnlyList<T>> Signals<T>(NeuronId publisher, CancellationToken ct) where T : Signal
            => Task.FromResult<IReadOnlyList<T>>([.. os.Journal(publisher.Brain).Signals(publisher).Select(entry => entry.Signal).OfType<T>()]);

        public Task ApproveAsPerson(string planId, CancellationToken ct) => os.Confirmations.Approve(planId);

        public Task Restart(CancellationToken ct) => os.Restart();

        public Task Deactivate(NeuronId id) => os.Deactivate(id);

        public Task SeedState(NeuronId id, string json, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(json);
            os.Journal(id.Brain).Seed(id, doc.RootElement.Clone());
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await os.DisposeAsync().ConfigureAwait(false);
            await serviceRoot.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class SiloRuntime(IInProcessHost host, BrainId brain) : INeuronRuntime
    {
        public static async Task<SiloRuntime> Start(BrainId brain, IReadOnlyList<Type> modules, Action<IServiceCollection> configure,
            IReadOnlyDictionary<string, string?> configuration, string? home, CancellationToken ct)
        {
            Assembly[] assemblies =
            [
                .. modules.Select(module => module.Assembly)
                    .Concat(modules.Select(module => module.GetProperty("Contracts", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Assembly).OfType<Assembly>())
                    .Append(typeof(Signal).Assembly)
                    .Distinct(),
            ];
            var factory = new LocalSiloHostFactory(new BrainStorageRegistration(assemblies), configure);
            var options = new InProcessHostOptions { Home = home, Modules = modules, Configuration = configuration };
            var host = await factory.Start(options, ct).ConfigureAwait(false);
            return new SiloRuntime(host, brain);
        }

        public NeuronRuntimeKind Kind => NeuronRuntimeKind.Silo;

        public BrainId Brain => brain;

        public IServiceProvider Services => host.Services;

        public DigitalBrain.Access.EdgeAuthority Edge => host.Services.GetRequiredService<DigitalBrain.Access.EdgeAuthority>();

        public T Get<T>(NeuronId id) where T : class => SiloNeurons.Get<T>(host.Services, id);

        public Task Idle() => Task.CompletedTask;

        public Task Elapse(TimeSpan by, CancellationToken ct) => Task.Delay(by, ct);

        public async Task<IReadOnlyList<T>> Signals<T>(NeuronId publisher, CancellationToken ct) where T : Signal
        {
            var journal = host.Services.GetRequiredService<IBrainJournalWriter>();
            await journal.Claim(publisher.Brain.Value, ct).ConfigureAwait(false);
            var page = await journal.ReadSince(publisher.Brain.Value, publisher.ToString(), 0, 1_000, ct).ConfigureAwait(false);
            var alias = AliasOf(typeof(T));
            return [.. page.Signals.Where(entry => entry.Type == alias).Select(entry => JsonSerializer.Deserialize<T>(entry.Json.Span, RuntimeProtocol.Json)!)];
        }

        public async Task ApproveAsPerson(string planId, CancellationToken ct)
        {
            var inbox = SiloNeurons.Get<DigitalBrain.Confirmation.IConfirmationInbox>(host.Services,
                new NeuronId(brain, "runtime.confirmations", DigitalBrain.Confirmation.IConfirmations.ConfirmationsNeuronKey));
            var edge = host.Services.GetRequiredService<DigitalBrain.Access.EdgeAuthority>();
            using var stamp = edge.Enter(edge.Person("person", "person", brain));
            await inbox.Resolve(planId, approve: true).WaitAsync(ct).ConfigureAwait(false);
        }

        public Task Restart(CancellationToken ct) => host.Restart(ct);

        public Task Deactivate(NeuronId id) => RuntimeTestHooks.Deactivate(host.Services, id);

        public async Task SeedState(NeuronId id, string json, CancellationToken ct)
        {
            var journal = host.Services.GetRequiredService<IBrainJournalWriter>();
            await journal.Claim(id.Brain.Value, ct).ConfigureAwait(false);
            var types = host.Services.GetService<NeuronTypeRegistry>();
            Type? neuronClass = null;
            var hasClass = types is not null && types.TryGet(id.Type, out neuronClass);
            var grainType = hasClass
                ? NeuronTypeRegistry.GrainTypeOf(NeuronTypeRegistry.PolicyOf(neuronClass!))
                : "neuron.host";
            var stateName = hasClass
                ? neuronClass!.GetCustomAttribute<DigitalBrain.Neurons.NeuronAttribute>(inherit: false)?.State ?? NeuronTypeRegistry.DefaultStateName
                : NeuronTypeRegistry.DefaultStateName;
            var grainId = Orleans.Runtime.GrainId.Create(grainType, id.ToString()).ToString();
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await journal.Append(
                id.Brain.Value,
                new JournalStateWrite(grainId, stateName, JournalStateChange.Write, null, bytes, null),
                id.ToString(),
                [],
                ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }
}
