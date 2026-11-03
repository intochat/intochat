using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Apps;

public sealed class AppSaveFacts
{
    public static TheoryData<NeuronRuntimeKind> Kinds => NeuronRuntimeKinds.Both;

    private sealed class SlowDeployRuntime(TaskCompletionSource entered, TaskCompletionSource proceed) : IAppRuntime
    {
        public int Deploys { get; private set; }
        public bool Accepts(AppBundle bundle) => true;

        public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> files, Dictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            Deploys++;
            entered.TrySetResult();
            await proceed.Task.WaitAsync(cancellationToken);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingRuntime : IAppRuntime
    {
        public int Deploys { get; private set; }
        public bool Accepts(AppBundle bundle) => true;

        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> files, Dictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            Deploys++;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [DigitalBrain.Module("flame-save-stub")]
    private sealed class FlameStubModule : DigitalBrain.Module
    {
        public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    private sealed class EmptyCapabilityConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class AppSaveFixture(IAppRuntime runtime) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule), typeof(FlameStubModule)];

        protected override void ConfigureTestOS(IServiceCollection services)
        {
            services.AddOptions<AppsOptions>();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton(runtime);
        }

        protected override void ConfigureSilo(IServiceCollection services)
        {
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.AddJournalPayloads(typeof(IApp).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(FlutterModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly);
            services.AddJournalPayloads(typeof(Activated).Assembly);
            services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
            services.TryAddSingleton<ICallFilter, CallFilter>();
            services.AddSingleton(runtime);
        }
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task ReadDuringSaveSeesCommittedState(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signalObserved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRuntime = new SlowDeployRuntime(entered, proceed);
        await using var fixture = new AppSaveFixture(slowRuntime);
        await using var runtime = await fixture.Start(kind, ct);

        var id = runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/notes-" + kind);
        var writer = runtime.Get<IApp>(id);
        var reader = runtime.Get<IApp>(id);

        await writer.Create(new AppManifest("Notes", "Notes", [], [], []), "v1");

        var listeners = runtime.Services.GetService<IClusterListeners>();
        IAsyncDisposable? subscription = null;
        if (listeners is not null)
        {
            subscription = await listeners.Listen<AppChanged>("observer", async (source, signal, _) =>
            {
                if (source == id && signal.Version == "0.1.1")
                {
                    // On Silo, ClusterListeners.Offer enqueues on the thread pool during HostNeuronState.Announce
                    // before Context.State.Save returns to Neuron<TState>.Save to assign State = next; yield once
                    // so the grain turn finishes its synchronous post-save assignment before we call [ReadOnly] Read().
                    await Task.Yield();
                    signalObserved.TrySetResult((await reader.Read()).Version);
                }
            });
        }

        var writeTask = writer.WriteBehavior("main.cs", "int x = 1;", Guid.NewGuid());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // While WriteBehavior is in flight (before Persist), Read returns the committed state from before the write.
        var midRead = await reader.Read();
        Assert.Equal("0.1.0", midRead.Version);
        Assert.Empty(midRead.Files);

        proceed.TrySetResult();
        var written = await writeTask;
        Assert.Equal("0.1.1", written.Version);

        var afterRead = await reader.Read();
        Assert.Equal("0.1.1", afterRead.Version);
        Assert.Single(afterRead.Files);

        if (subscription is not null)
        {
            var versionSeenOnSignal = await signalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("0.1.1", versionSeenOnSignal);
            await subscription.DisposeAsync();
        }
    }

    [Fact]
    public async Task RestoreRunsAsItsOwnTurnAfterActivation()
    {
        var counting = new CountingRuntime();
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAppRuntime>(counting);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();

        var brain = new BrainId("facts");
        var id = new NeuronId(brain, IntoChat.OS.Connections.AppGrainType.Name, "apps/restore-test");
        var app = os.Get<IApp>(id);
        var bundle = new AppBundle("restore-test", "1.0.0", new AppManifest("Restore", "Restore", [], [], []), "spec",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["main.cs"] = "int x = 1;" });
        await app.Import(bundle);
        Assert.Equal(1, counting.Deploys);

        await os.Deactivate(id);
        await os.Activate(id);
        await os.Idle();

        Assert.Equal(2, counting.Deploys);
    }
}
