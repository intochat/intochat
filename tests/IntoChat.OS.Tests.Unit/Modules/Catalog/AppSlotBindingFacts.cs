using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class AppSlotBindingFacts
{
    private static readonly BrainId Brain = new("owner");

    private static AppManifest AgentManifest() => new(
        Title: "Treg",
        Description: "Monitors treg",
        Operations: [],
        Settings: [],
        Slots: [new AppSlot("treg", Connector: "mcp", Required: true)],
        Agent: new AppAgent("You help with treg.", Starters: ["List issues"], Tools: ["treg"]));

    private static TestOS CreateOs()
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        var os = TestOS.Local(o => o.Services = services.BuildServiceProvider());
        os.Register<AppNeuron>();
        os.Register<ConfirmationsNeuron>();
        return os;
    }

    private static IApp GetApp(TestOS os, string name)
        => os.Get<IApp>(new NeuronId(Brain, AppGrainType.Name, $"apps/{name}"));

    [Fact]
    public async Task PureDataAgentAppTransitionsDirectlyToLiveOnCreateWhilePlainAppStaysDraft()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", Brain));

        var agentSnap = await GetApp(os, "treg").Create(AgentManifest(), "");
        Assert.Equal(AppStatus.Live, agentSnap.Status);
        Assert.NotNull(agentSnap.Manifest.Agent);

        var plainSnap = await GetApp(os, "notes").Create(
            new AppManifest("Notes", "Plain app", [], [], []),
            "spec");
        Assert.Equal(AppStatus.Drafted, plainSnap.Status);
    }

    [Fact]
    public async Task DirectPersonBindSlotsImmediatelyPersistsCanonicalConnectionKey()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", Brain));

        var app = GetApp(os, "treg");
        await app.Create(AgentManifest(), "");

        const string canonicalKey = "owner/connection/mcp/treg-main";
        var outcome = await app.BindSlots(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["treg"] = canonicalKey },
            TestContext.Current.CancellationToken);

        Assert.Equal(SlotBindingStatus.Ok, outcome.Status);
        Assert.NotNull(outcome.App);
        Assert.Equal(canonicalKey, outcome.App.Connections["treg"]);

        var read = await app.Read();
        Assert.Equal(canonicalKey, read.Connections["treg"]);
    }

    [Fact]
    public async Task NonPersonBindSlotsRequiresHumanConfirmationAndBindsUponHumanApproval()
    {
        await using var os = CreateOs();
        var app = GetApp(os, "treg");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            await app.Create(AgentManifest(), "");
        }

        const string canonicalKey = "owner/connection/mcp/treg-prod";
        SlotBindingOutcome outcome;
        using (os.Edge.Enter(os.Edge.App("owner/apps/ino", new Actor.Person("owner", "owner"), Brain)))
        {
            outcome = await app.BindSlots(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["treg"] = canonicalKey },
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(SlotBindingStatus.PendingConfirmation, outcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(outcome.ConfirmationId));

        // Slot must NOT be bound before human approval.
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            var before = await app.Read();
            Assert.False(before.Connections.ContainsKey("treg"));

            var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(Brain));
            await inbox.Resolve(outcome.ConfirmationId!, approve: true);
            await os.Idle();

            var after = await app.Read();
            Assert.Equal(canonicalKey, after.Connections["treg"]);
        }
    }

    [Fact]
    public async Task NonPersonBindSlotsDeniedLeavesSlotUnbound()
    {
        await using var os = CreateOs();
        var app = GetApp(os, "treg");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            await app.Create(AgentManifest(), "");
        }

        SlotBindingOutcome outcome;
        using (os.Edge.Enter(os.Edge.App("owner/apps/ino", new Actor.Person("owner", "owner"), Brain)))
        {
            outcome = await app.BindSlots(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["treg"] = "owner/connection/mcp/treg-prod" },
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(SlotBindingStatus.PendingConfirmation, outcome.Status);

        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(Brain));
            await inbox.Resolve(outcome.ConfirmationId!, approve: false);
            await os.Idle();

            var after = await app.Read();
            Assert.False(after.Connections.ContainsKey("treg"));
        }
    }

    [Fact]
    public async Task NonPersonConfigureCannotModifyConnections()
    {
        await using var os = CreateOs();
        var app = GetApp(os, "treg");
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            await app.Create(AgentManifest(), "");
        }

        using (os.Edge.Enter(os.Edge.App("owner/apps/ino", new Actor.Person("owner", "owner"), Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.Configure(
                new Dictionary<string, string>(),
                new Dictionary<string, string> { ["treg"] = "owner/connection/mcp/treg-prod" }));
        }
    }

    [Fact]
    public async Task ShortIdCanonicalizesForConnectorSlotAndRefusesInvalidOrUnresolvedCapabilityKeys()
    {
        await using var os = CreateOs();
        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", Brain));
        var ct = TestContext.Current.CancellationToken;

        var app = GetApp(os, "treg");
        await app.Create(
            new AppManifest(
                Title: "Treg",
                Description: "Monitors treg",
                Operations: [],
                Settings: [],
                Slots:
                [
                    new AppSlot("treg", Connector: "mcp", Required: true),
                    new AppSlot("files", Capability: "storage@1", Required: false),
                ],
                Agent: new AppAgent("You help with treg.", Tools: ["treg"])),
            "");

        // Short id for explicit connector slot canonicalizes to owner/connection/mcp/<id>.
        var shortConnector = await app.BindSlots(new Dictionary<string, string>(StringComparer.Ordinal) { ["treg"] = "treg-short" }, ct);
        Assert.Equal(SlotBindingStatus.Ok, shortConnector.Status);
        Assert.Equal("owner/connection/mcp/treg-short", shortConnector.App!.Connections["treg"]);

        // Short id for capability slot without catalog match is refused.
        var unresolvedCap = await app.BindSlots(new Dictionary<string, string>(StringComparer.Ordinal) { ["files"] = "not-a-canonical-key" }, ct);
        Assert.Equal(SlotBindingStatus.Refused, unresolvedCap.Status);

        // Wrong brain canonical key is refused.
        var wrongBrain = await app.BindSlots(new Dictionary<string, string>(StringComparer.Ordinal) { ["treg"] = "other/connection/mcp/treg-short" }, ct);
        Assert.Equal(SlotBindingStatus.Refused, wrongBrain.Status);

        // Wrong connector canonical key is refused.
        var wrongConnector = await app.BindSlots(new Dictionary<string, string>(StringComparer.Ordinal) { ["treg"] = "owner/connection/github/repo" }, ct);
        Assert.Equal(SlotBindingStatus.Refused, wrongConnector.Status);
    }
}
