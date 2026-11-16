using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Flutter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Ino;

// The synapse studio on a real silo: the framework's synapse host compiles, analyzes, deploys and restores the units.
public sealed class SynapseStudioFacts
{
    // The first unit: every five minutes the tick reaction counts a poll and publishes when it ran. The framework's schedule text
    // for a five-minute interval is "every 5m" ("00:05:00" is not a schedule), and Every needs a reaction to DigitalBrain.Time.Tick.
    internal const string TickPoller = """
        using DigitalBrain.Time;

        namespace Ino.Units;

        [Signal("ino.tick-poller.polled")]
        public sealed record Polled(DateTimeOffset At) : Signal;

        [State("ino.tick-poller")]
        public sealed record PollerState(int Polls = 0)
        {
            public PollerState() : this(0) { }
        }

        public static class TickPoller
        {
            [Synapse(typeof(Tick), "ino.tick-poller", Every = "every 5m")]
            public static Reaction<PollerState> Poll(Tick tick, PollerState state) =>
                Reaction<PollerState>.Of(state with { Polls = state.Polls + 1 }, new Polled(tick.At));
        }
        """;

    // A status-like member on synapse state: the StatusString analyzer (DB0043) reports it.
    internal static readonly string WithStatus = TickPoller.Replace("PollerState(int Polls = 0)", "PollerState(int Polls = 0, string Status = \"\")", StringComparison.Ordinal)
        .Replace(": this(0)", ": this(0, \"\")", StringComparison.Ordinal);

    private const string Alias = "tick-poller";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed class EmptyCapabilityConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // Counts deploys; a stand-in for the silo's host where a fact proves a deploy is not repeated.
    private sealed class CountingHost : ISynapseHost
    {
        public int Deploys { get; private set; }

        public Task<DeployOutcome> Deploy(BrainId brain, SynapseSource source, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindingsPerAlias, CancellationToken ct)
        {
            Deploys++;
            return Task.FromResult(new DeployOutcome(DeployStatus.Deployed, "h" + Deploys, [], new Dictionary<string, DigitalBrain.Units.InstallOutcome>(), null));
        }

        public Task<DeployOutcome> Retire(BrainId brain, string sourceKey, CancellationToken ct)
            => Task.FromResult(new DeployOutcome(DeployStatus.Retired, null, [], new Dictionary<string, DigitalBrain.Units.InstallOutcome>(), null));

        public Task<IReadOnlyList<DeployedSource>> List(BrainId brain, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeployedSource>>([]);
    }

    internal sealed class StudioFixture : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.AddJournalPayloads(typeof(IApp).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlutterModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly);
            services.AddJournalPayloads(typeof(DigitalBrain.Signals.Activated).Assembly);
            services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
            services.TryAddSingleton(TimeProvider.System);
        }
    }

    private static ISynapseStudio Studio(INeuronRuntime runtime) => runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));

    [Fact]
    public async Task CheckReportsAnalyzerDiagnostics()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        var clean = await Studio(runtime).Check(TickPoller);
        var flagged = await Studio(runtime).Check(WithStatus);

        Assert.StartsWith("No diagnostics", clean.Text, StringComparison.Ordinal);
        Assert.Contains("DB0043", flagged.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsCompileFailure()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        var clean = await Studio(runtime).Check(TickPoller);
        var broken = await Studio(runtime).Check("this is not C# {");

        Assert.True(clean.Ok, clean.Text);
        Assert.False(broken.Ok);
        Assert.StartsWith("Does not compile", broken.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCsharpRunsSynapseAnalyzersOnSynapseSource()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        var check = SynapseCheck.Compile(runtime.Services, new Dictionary<string, string> { ["poller.cs"] = WithStatus });

        Assert.NotNull(check);
        Assert.Contains(check.Errors, error => error.Id == "DB0043");
        Assert.Null(SynapseCheck.Compile(runtime.Services, new Dictionary<string, string> { ["main.cs"] = "int x = 1;" }));
    }

    [Fact]
    public async Task AHeadlessBuildDeploysAUnitAndWaitsForItsNeeds()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/" + Alias));
        await app.Create(new AppManifest("Tick poller", "Polls every five minutes", [], [], [], Headless: true), "spec");

        await app.BeginBuild(null);
        var attempt = await app.CurrentBuildAttempt();

        var failure = await UnitDeploy.Complete(app, attempt, Studio(runtime), Alias, new Dictionary<string, string> { ["poller.cs"] = TickPoller }, new Dictionary<string, string>());

        Assert.Null(failure);
        var unit = Assert.Single(await Studio(runtime).List());
        Assert.Equal(Alias, unit.SourceKey);
        Assert.Contains("ino.tick-poller", unit.Aliases);
        var snapshot = await app.Read();
        // The unit is deployed, but its schedule is a new need: the app stays Building until the person approves it.
        Assert.Equal(AppStatus.Building, snapshot.Status);
        Assert.Empty(snapshot.Files);
        Assert.StartsWith("waiting for approval: ", snapshot.Activity[^1], StringComparison.Ordinal);
    }

    // Builder output is several files; each keeps its own file-scoped namespace.
    [Fact]
    public async Task AUnitOfTwoFilesDeploysWithEachNamespace()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var signals = """
            namespace Ino.Units.Signals;

            [Signal("ino.two-file.polled")]
            public sealed record Polled(DateTimeOffset At) : Signal;
            """;
        var poller = """
            using DigitalBrain.Time;
            using Ino.Units.Signals;

            namespace Ino.Units;

            [State("ino.two-file")]
            public sealed record PollerState(int Polls = 0)
            {
                public PollerState() : this(0) { }
            }

            public static class TwoFilePoller
            {
                [Synapse(typeof(Tick), "ino.two-file", Every = "every 5m")]
                public static Reaction<PollerState> Poll(Tick tick, PollerState state) =>
                    Reaction<PollerState>.Of(state with { Polls = state.Polls + 1 }, new Polled(tick.At));
            }
            """;

        var deployed = await Studio(runtime).DeployFiles("two-file", new Dictionary<string, string> { ["signals.cs"] = signals, ["poller.cs"] = poller }, new Dictionary<string, string>());

        Assert.True(deployed.Ok, deployed.Text);
        Assert.Contains("ino.two-file", Assert.Single(await Studio(runtime).List()).Aliases);
    }

    [Fact]
    public async Task DeployThenListShowsTheUnit()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        var deployed = await Studio(runtime).Deploy(Alias, TickPoller);

        var unit = Assert.Single(await Studio(runtime).List());
        Assert.Equal((Alias, DeployStatus.Deployed), (unit.SourceKey, unit.Status));
        Assert.Contains(unit.Hash, deployed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUnitSurvivesSiloRestart()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Studio(runtime).Deploy(Alias, TickPoller);
        var before = Assert.Single(await Studio(runtime).List());

        await runtime.Restart(Ct);

        var after = Assert.Single(await Studio(runtime).List());
        Assert.Equal((before.SourceKey, before.Hash), (after.SourceKey, after.Hash));
    }

    [Fact]
    public async Task DeployedUnitsSettingsAndAppSnapshotsSurviveSiloRestartWithoutDataLoss()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        // 1. Deploy a multi-file unit ("two-file") and a scheduled tick unit ("tick-poller")
        var signals = """
            namespace Ino.Units.Signals;

            [Signal("ino.two-file.polled")]
            public sealed record Polled(DateTimeOffset At) : Signal;
            """;
        var poller = """
            using DigitalBrain.Time;
            using Ino.Units.Signals;

            namespace Ino.Units;

            [State("ino.two-file")]
            public sealed record PollerState(int Polls = 0)
            {
                public PollerState() : this(0) { }
            }

            public static class TwoFilePoller
            {
                [Synapse(typeof(Tick), "ino.two-file", Every = "every 5m")]
                public static Reaction<PollerState> Poll(Tick tick, PollerState state) =>
                    Reaction<PollerState>.Of(state with { Polls = state.Polls + 1 }, new Polled(tick.At));
            }
            """;
        var twoFileDeploy = await Studio(runtime).DeployFiles("two-file", new Dictionary<string, string> { ["signals.cs"] = signals, ["poller.cs"] = poller }, new Dictionary<string, string>());
        Assert.True(twoFileDeploy.Ok, twoFileDeploy.Text);
        var tickDeploy = await Studio(runtime).Deploy(Alias, TickPoller);
        Assert.True(tickDeploy.Ok, tickDeploy.Text);

        // 2. Persist conversation registry state in InoConversationsNeuron
        var conversations = runtime.Get<IInoConversations>(runtime.Id("ino.conversations", IInoConversations.DefaultKey));
        await conversations.Use("ino/conversations/c-resilience-1");

        // 3. Create and configure an app snapshot on the silo
        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/resilience-probe"));
        await app.Create(new AppManifest("Resilience Probe", "Verifies state across restart", [], [], [], Headless: true), "probe-spec");

        // 4. Simulate host/silo restart
        await runtime.Restart(Ct);

        // 5. Verify all deployed units, conversation registry, and app state recovered deterministically without data loss
        var deployedAfter = await Studio(runtime).List();
        Assert.Equal(2, deployedAfter.Length);
        Assert.Contains(deployedAfter, u => u.SourceKey == "two-file" && u.Status == DeployStatus.Deployed);
        Assert.Contains(deployedAfter, u => u.SourceKey == Alias && u.Status == DeployStatus.Deployed);

        var currentAgents = await runtime.Get<IInoConversations>(runtime.Id("ino.conversations", IInoConversations.DefaultKey)).Current();
        Assert.Equal(["ino/conversations/c-resilience-1"], currentAgents);

        var appAfter = await runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/resilience-probe")).Read();
        Assert.Equal("Resilience Probe", appAfter.Manifest.Title);
        Assert.Equal("probe-spec", appAfter.Spec);
    }

    [Fact]
    public async Task StagedSynapseApprovalAndHeadlessBuildStateSurviveSiloRestart()
    {
        await using var fixture = new StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        var app = runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/" + Alias));
        await app.Create(new AppManifest("Tick poller", "Polls every five minutes", [], [], [], Headless: true), "spec");
        await app.BeginBuild(null);
        var attempt = await app.CurrentBuildAttempt();

        var failure = await UnitDeploy.Complete(app, attempt, Studio(runtime), Alias, new Dictionary<string, string> { ["poller.cs"] = TickPoller }, new Dictionary<string, string>());
        Assert.Null(failure);

        IReadOnlyList<IntoChat.OS.Modules.Apps.Approvals.Approval> approvalsBefore;
        using (RuntimeCallerContext.StampScope(CapabilityCallers.Platform(runtime.Brain.Value, runtime.Brain.Value, runtime.Brain.Value)))
        {
            approvalsBefore = await runtime.Get<IntoChat.OS.Modules.Apps.Approvals.IApprovals>(runtime.Id("approvals", "approvals/index")).Pending(Ct);
        }
        var stagedConsentBefore = Assert.Single(approvalsBefore, a => a.Kind == IntoChat.OS.Modules.Apps.Approvals.ApprovalKind.SynapseStaged);

        await runtime.Restart(Ct);

        IReadOnlyList<IntoChat.OS.Modules.Apps.Approvals.Approval> approvalsAfter;
        using (RuntimeCallerContext.StampScope(CapabilityCallers.Platform(runtime.Brain.Value, runtime.Brain.Value, runtime.Brain.Value)))
        {
            approvalsAfter = await runtime.Get<IntoChat.OS.Modules.Apps.Approvals.IApprovals>(runtime.Id("approvals", "approvals/index")).Pending(Ct);
        }
        var stagedConsentAfter = Assert.Single(approvalsAfter, a => a.Kind == IntoChat.OS.Modules.Apps.Approvals.ApprovalKind.SynapseStaged);
        Assert.Equal((stagedConsentBefore.PlanId, stagedConsentBefore.Subject), (stagedConsentAfter.PlanId, stagedConsentAfter.Subject));

        var snapshotAfter = await runtime.Get<IApp>(runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/" + Alias)).Read();
        Assert.Equal(AppStatus.Building, snapshotAfter.Status);
        Assert.StartsWith("waiting for approval: ", snapshotAfter.Activity[^1], StringComparison.Ordinal);
    }
}
