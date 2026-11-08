using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Apps;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Ino;

// A circle brain is born with its room: the shipped circleroom unit deploys through the synapse studio on a real silo. It asks for
// no slots; its reactions and view are an ask, and only that ask is approved, as the creator (the framework takes no platform
// approval).
public sealed class CircleRoomInstallFacts
{
    // An authored unit with a view: its install waits for the person.
    private const string Notes = """
        namespace Notes;

        [State("notes")]
        public sealed record NotesState(int Count = 0)
        {
            public NotesState() : this(0) { }
        }

        public static class NotesUnit
        {
            [Synapse("notes")]
            public static DigitalBrain.UI.View Show(NotesState state) => new("Notes", new DigitalBrain.UI.Text("Notes"));
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Brain(ISynapseStudio Studio, IInstalls Installs, IConfirmationInbox Inbox, List<string> Activity)
    {
        public Task<UnitDeployResult> Ship(string alias) => ShippedUnits.Deploy(Studio, alias, async _ => await Installs.Pending(CancellationToken.None),
            plan => Inbox.Resolve(plan, true), Activity.Add);
    }

    private static Brain Of(INeuronRuntime runtime) => new(
        runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value)),
        runtime.Get<IInstalls>(new NeuronId(runtime.Brain, "synapse.installs", "default")),
        runtime.Get<IConfirmationInbox>(new NeuronId(runtime.Brain, "runtime.confirmations", "confirmations")),
        []);

    // Born as its creator: the birth handler runs in the creator's turn.
    private static IDisposable AsCreator(INeuronRuntime runtime) => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value));

    [Fact]
    public async Task ANewCircleBrainHasTheRoomInstalled()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var creator = AsCreator(runtime);
        var brain = Of(runtime);

        foreach (var alias in ShippedApps.CircleUnits)
        {
            var result = await brain.Ship(alias);
            Assert.True(result.Ok, result.Text);
            Assert.True(result.Status is DeployStatus.Deployed or DeployStatus.Unchanged, result.Status.ToString());
            Assert.True(result.Installs[alias].Status is DigitalBrain.InstallStatus.Installed or DigitalBrain.InstallStatus.Unchanged, result.Installs[alias].Sentence);
        }

        // The room compiles inside the synapse allow-list: no diagnostics, none of them DB0045.
        var check = await brain.Studio.Check(ShippedUnits.Files("circleroom")["CircleRoom.cs"]);
        Assert.True(check.Ok, check.Text);
        Assert.DoesNotContain("DB0045", check.Text, StringComparison.Ordinal);
        var unit = Assert.Single(await brain.Studio.List());
        Assert.Equal("circleroom", unit.SourceKey);
        Assert.NotNull(await brain.Installs.Describe("circleroom", Ct));
        Assert.Equal(["scenarios.json", "spec.md"], (await brain.Studio.Sidecars("circleroom")).Keys.Order(StringComparer.Ordinal));
        // The activity names the unit and what it asks for.
        Assert.Contains(brain.Activity, line => line.Contains("circleroom", StringComparison.Ordinal) && line.Contains("show a view", StringComparison.Ordinal));
        // Born twice (a retried Create, a later connect): the same files deploy once and stay installed.
        Assert.True((await brain.Ship("circleroom")).Ok);
        Assert.Single(await brain.Studio.List());
    }

    [Fact]
    public async Task ShippedUnitsNeverApproveAForeignPendingInstall()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var creator = AsCreator(runtime);
        var brain = Of(runtime);
        var authored = await brain.Studio.Deploy("notes", Notes);
        Assert.True(authored.Ok, authored.Text);
        var waiting = Assert.Single(await brain.Installs.Pending(Ct), ask => ask.Alias == "notes");

        Assert.True((await brain.Ship("circleroom")).Ok);

        Assert.Contains(await brain.Installs.Pending(Ct), ask => ask.PlanId == waiting.PlanId && ask.Alias == "notes");
        Assert.Null(await brain.Installs.Describe("notes", Ct));
        Assert.NotNull(await brain.Installs.Describe("circleroom", Ct));
    }

    // Why the creator approves: the confirmations record a platform caller's approval as a denial, so the room stays waiting.
    [Fact]
    public async Task APlatformCallerCannotApproveAShippedUnitsAsk()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var platform = RuntimeCallerContext.Stamp(CapabilityCallers.Platform("os.shipped-units", "system", runtime.Brain.Value));
        var brain = Of(runtime);

        await brain.Ship("circleroom");

        Assert.Null(await brain.Installs.Describe("circleroom", Ct));
    }
}
