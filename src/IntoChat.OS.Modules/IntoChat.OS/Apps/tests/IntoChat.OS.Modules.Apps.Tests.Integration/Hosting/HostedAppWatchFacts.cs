using DigitalBrain;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Runtime.Apps;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed record HostedWatchState(string[] Heard)
{
    public HostedWatchState() : this([]) { }
}

/*
# Hosted Watch
Role: Test
Shipped: false
Operations:

Records each data change its set watch hears from the current ino.conversations member.
*/
[DigitalBrain.App("hosted-watch")]
public sealed class HostedWatchApp : App<HostedWatchState>
{
    public static TaskCompletionSource<string>? Heard { get; set; }

    protected override void Watches(WatchBuilder watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        watch.OnEach<IInoConversations, InoConversationsChanged, IInoData, InoDataChanged>(
            "data",
            _ => IInoConversations.DefaultKey,
            (registry, _, _) => registry.Current(),
            changed =>
            {
                Set(State with { Heard = [.. State.Heard, changed.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)] });
                return Task.CompletedTask;
            });
    }
}

// On a real silo: watches ride ISignalWatch session subscriptions. Members are ino.data neurons, which publish InoDataChanged on Connect.
public sealed class HostedAppWatchFacts : IAsyncLifetime
{
    private IInProcessHost? _host;
    private static readonly System.Collections.Concurrent.ConcurrentBag<Exception> Errors = [];

    private IServiceProvider Services => (_host ?? throw new InvalidOperationException("Host not started.")).Services;

    public async ValueTask InitializeAsync()
    {
        var signalAssemblies = new[]
        {
            typeof(AppsModule).Assembly,
            typeof(IApp).Assembly,
            typeof(FlutterModule).Assembly,
            typeof(IView).Assembly,
            typeof(DigitalBrain.Signals.Activated).Assembly,
        }.Distinct().ToArray();
        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration(signalAssemblies),
            services =>
            {
                AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
                services.TryAddSingleton<ICapabilityConnections, NoConnections>();
                services.TryAddSingleton<ICallFilter, CallFilter>();
                services.AddIntoChatOSClient();
            });
        _host = await factory.Start(new InProcessHostOptions { Modules = [typeof(AppsModule), typeof(FlutterModule), typeof(FlameStub)] }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) { await _host.DisposeAsync(); }
    }

    [Fact]
    public async Task HostedAppReceivesSetWatchSignalsAndFollowsRotation()
    {
        const string brain = "watchbrain";
        using var _ = Stamp(brain);
        var definition = new AppDefinition(typeof(HostedWatchApp));
        var app = (HostedWatchApp)definition.Create(_ => null);
        var turns = new AppTurns(app, definition);
        await Use(brain, "ino/data-a");
        await using var watches = HostedAppWatches.Attach(Services.GetRequiredService<IIntoChatOSBrains>().Open(brain), turns,
            new AppAddress(brain, IApp.Key(brain, "hosted-watch")), (contract, id) => SiloNeurons.Get(Services, contract, id), (error, _) => Errors.Add(error));

        await AssertRotationAsync(brain, () => app.State.Heard);
    }

    [Fact]
    public async Task CompiledAppsHostDeliversWatchesToAHostedApp()
    {
        const string brain = "hostbrain";
        using var _ = Stamp(brain);
        var appKey = IApp.Key(brain, "hosted-watch");
        await Use(brain, "ino/data-a");
        await Services.GetRequiredService<IntoChat.OS.Modules.Apps.Hosting.InProcess.InProcessAppHost>().AssignType(appKey, typeof(HostedWatchApp), new Dictionary<string, string>(StringComparer.Ordinal), TestContext.Current.CancellationToken);
        var compiled = ActivatorUtilities.CreateInstance<CompiledAppsHost>(Services);

        var turns = await compiled.EnsureForTestAsync(brain, "hosted-watch", TestContext.Current.CancellationToken);

        Assert.NotNull(turns);
        await AssertRotationAsync(brain, () => ((HostedWatchApp)turns.App).State.Heard);
    }

    private async Task AssertRotationAsync(string brain, Func<string[]> heard)
    {
        var a = SiloNeurons.Get<IInoData>(Services, $"{brain}/ino/data-a");
        var b = SiloNeurons.Get<IInoData>(Services, $"{brain}/ino/data-b");
        // Subscribing is asynchronous too: a's change is published until the first subscription is in.
        await Eventually(async () =>
        {
            await a.Connect("Host=a;Database=a");
            return heard().Length >= 1;
        });

        await Use(brain, "ino/data-b");
        // The re-selection is asynchronous: b's change is published until the set has moved to it.
        await Eventually(async () =>
        {
            await b.Connect("Host=b;Database=b");
            return heard().Length >= 2;
        });
        // Let the retries drain, then only the current member's change is heard.
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        var count = heard().Length;
        await a.Connect("Host=a;Database=a2");
        await b.Connect("Host=b;Database=b2");
        await Eventually(() => heard().Length >= count + 1);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(count + 1, heard().Length);
    }

    private Task Use(string brain, string member) => SiloNeurons.Get<IInoConversations>(Services, $"{brain}/{IInoConversations.DefaultKey}").Use(member);

    private static Task Eventually(Func<bool> done) => Eventually(() => Task.FromResult(done()));

    private static async Task Eventually(Func<Task<bool>> done)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await done())
        {
            if (DateTimeOffset.UtcNow > until) { throw new TimeoutException("The watch did not deliver in time. " + string.Join(" | ", Errors.Select(e => e.ToString()))); }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static IDisposable Stamp(string brain) => RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

    [DigitalBrain.Module("flame-stub-watch")]
    private sealed class FlameStub : DigitalBrain.Module
    {
        public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    private sealed class NoConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default) where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default) => Task.CompletedTask;
    }
}
