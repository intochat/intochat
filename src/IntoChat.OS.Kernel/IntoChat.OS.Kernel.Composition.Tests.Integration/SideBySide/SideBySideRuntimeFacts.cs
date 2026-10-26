using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.Orleans;
using IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using DB = DigitalBrain;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.SideBySide;

// Both kinds of neuron in one silo over one BrainLog per brain (06-02, R4-09, R4-12): a plain [Neuron] on the runtime's host
// grains and a legacy Framework grain, whose state and signals the legacy adapters write through IBrainJournalWriter.
[Trait("host", "own")]
public sealed class SideBySideRuntimeFacts
{
    private const string Brain = "owner";

    private static IPlainProbe Plain(ITestBrain brain, string key)
        => NeuronProxy.Create<IPlainProbe>(DB.NeuronId.Parse($"{Brain}/probe.plain/{key}"), new LegacyGrainSink(brain.Grains(), "owner/test.edge/facts"));

    [Fact(Timeout = 120_000)]
    public async Task APlainAndALegacyNeuronServeInOneSiloAndTheLegacyStateSurvivesARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>().StartAsync(ct);
        var legacy = brain.Grains().GetGrain<ILegacyProbe>($"{Brain}/s1");

        Assert.Equal("x|owner/test.edge/facts", await Plain(brain, "s1").Echo("x"));
        await legacy.Ping("one");
        await legacy.Ping("two");
        await brain.RestartAsync(ct);

        Assert.Equal(2, await brain.Grains().GetGrain<ILegacyProbe>($"{Brain}/s1").Pings());
        var journal = brain.Services.GetRequiredService<ISignalJournal>();
        var part = await journal.SignalsOf(brain.Grains().GetGrain<ILegacyProbe>($"{Brain}/s1").GetGrainId(), 0);
        Assert.Equal(["one", "two"], part!.Signals.Select(item => ((LegacyPinged)item.Signal).Text));

        // 06-06d: the kernel lists the legacy neuron beside a plain one.
        await Counted(brain, "p1").Add();
        Assert.Equal([$"{Brain}/s1"], await Listed(brain, LegacyProbe.Type, ct));
        Assert.Equal([$"{Brain}/p1"], await Listed(brain, PlainCounted.Type, ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task APlainSubscriberReceivesALegacyPublishersSignalOnceAndAgainAfterARestartWithoutDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>().StartAsync(ct);
        var subscriber = $"{Brain}/probe.plain/listener";
        await Plain(brain, "listener").Follow($"{Brain}/{LegacyProbe.Type}/s2");

        await brain.Grains().GetGrain<ILegacyProbe>($"{Brain}/s2").Ping("first");
        await Heard.Until(subscriber, 1, ct);
        await brain.RestartAsync(ct);
        await brain.Grains().GetGrain<ILegacyProbe>($"{Brain}/s2").Ping("second");
        await Heard.Until(subscriber, 2, ct);
        await Task.Delay(500, ct);

        Assert.Equal(["first", "second"], Heard.Of(subscriber));
    }

    [Fact(Timeout = 120_000)]
    public async Task OsReadersReadTheLegacySignalsOfTheSharedLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>().StartAsync(ct);
        await brain.Grains().GetGrain<ILegacyProbe>($"{Brain}/s3").Ping("read");

        var read = await brain.Services.GetRequiredService<ISignalJournal>().ReadSince(Brain, 0, 100);

        Assert.Contains(read, item => item.Signal is LegacyPinged { Text: "read" });
        Assert.NotNull(brain.Services.GetRequiredService<IBrainJournalWriter>());
    }

    // A module whose manifest needs what the selected topology does not bind stops the host, naming the module and the need.
    [Fact]
    public void AnUnboundNeedStopsTheHostNamingTheModuleAndTheNeed()
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Builder(home, new Dictionary<string, string?>());

        var error = Assert.Throws<InvalidOperationException>(() => builder.AddIntoChatOSServer(server => server
            .AddModule(new SandboxlessLocal()).AddModule<OrleansModule>().AddNeuronModule<SandboxProbeModule>()));

        Assert.Contains("probe-sandbox", error.Message, StringComparison.Ordinal);
        Assert.Contains("Sandbox", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundNeedComposes()
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Builder(home, new Dictionary<string, string?>());

        builder.AddIntoChatOSServer(server => server.AddModule<OrleansModule>().AddNeuronModule<SandboxProbeModule>());

        Assert.True(KernelStorage.IsComposed(builder.Services) || ClusterStoreHosting.HasClusterStore(builder.Services));
    }

    // The local topology without a sandbox binding (test only).
    private sealed class SandboxlessLocal() : Module(new ModuleId("sandboxless-local")), ITopologyModule
    {
        public IReadOnlyList<ITopologyProfile> TopologyProfiles { get; } = [new Profile()];

        public override void Configure(ISiloBuilder silo) { }

        private sealed class Profile : ITopologyProfile
        {
            private readonly LocalProfile _inner = new();
            public Topology Topology => _inner.Topology;
            public Bindings Bindings => LocalProfile.LocalBindings with { Sandbox = SandboxBinding.None };
            public Services Services => _inner.Services;
            public IReadOnlyDictionary<string, string?> Defaults => _inner.Defaults;
            public IEnumerable<TopologyIssue> Validate(Microsoft.Extensions.Configuration.IConfiguration configuration, IReadOnlyList<DB.ModuleManifest> modules) => _inner.Validate(configuration, modules);
            public void ConfigureConfiguration(Microsoft.Extensions.Configuration.IConfigurationBuilder builder, TopologyContext context) => _inner.ConfigureConfiguration(builder, context);
            public void ConfigureSilo(ISiloBuilder silo, TopologyContext context) => _inner.ConfigureSilo(silo, context);
            public void ConfigureServices(IServiceCollection services, TopologyContext context) => _inner.ConfigureServices(services, context);
        }
    }

    // 06-06d (R4-21(4)): the kernel's instance listing sees both kinds of neuron. A legacy grain registers on its first write
    // through the storage decorator; a plain neuron registers once a call leaves it with state, never for a read-only call, and
    // retires when its state is cleared.
    private static IPlainCounted Counted(ITestBrain brain, string key)
        => brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<IPlainCounted>($"{Brain}/{key}");

    private static async Task<string[]> Listed(ITestBrain brain, string type, CancellationToken ct)
    {
        List<string> keys = [];
        await foreach (var instance in brain.Services.GetRequiredService<Kernel>().InstancesIn(Brain, type, ct)) { keys.Add(instance.Key); }
        return [.. keys.Order(StringComparer.Ordinal)];
    }

    [Fact(Timeout = 120_000)]
    public async Task APlainNeuronIsListedOnceItHasStateAndRetiredWhenItIsCleared()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>().StartAsync(ct);

        Assert.Equal(0, await Counted(brain, "read-only").Count());
        await Counted(brain, "a").Add();
        await Counted(brain, "a").Add();
        await Counted(brain, "b").Add();
        Assert.Equal([$"{Brain}/a", $"{Brain}/b"], await Listed(brain, PlainCounted.Type, ct));

        await Counted(brain, "a").Forget();
        Assert.Equal([$"{Brain}/b"], await Listed(brain, PlainCounted.Type, ct));
    }
}
