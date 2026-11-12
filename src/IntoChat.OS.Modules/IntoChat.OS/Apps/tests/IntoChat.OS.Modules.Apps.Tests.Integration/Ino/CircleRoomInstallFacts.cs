using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Ino;

// A circle brain is born with its room deployed and waiting: the shipped circleroom unit lands through the synapse studio on a real
// silo, and its reactions and view wait for the creator's consent in the approvals. The OS never approves it.
public sealed class CircleRoomInstallFacts
{
    private const string Alias = "circleroom";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Brain(INeuronRuntime Runtime)
    {
        public ISynapseStudio Studio => Runtime.Get<ISynapseStudio>(ISynapseStudio.Key(Runtime.Brain.Value));
        public IInstalls Installs => Runtime.Get<IInstalls>(new NeuronId(Runtime.Brain, "synapse.installs", "default"));
        public NeuronId Confirmations => new(Runtime.Brain, "runtime.confirmations", "confirmations");
        public IConfirmationInbox Inbox => Runtime.Get<IConfirmationInbox>(Confirmations);
        public IApp App => Runtime.Get<IApp>(Runtime.Id(IntoChat.OS.Connections.AppGrainType.Name, "apps/" + Alias));
        public IApprovals Approvals => Runtime.Get<IApprovals>(IApprovals.Key(Runtime.Brain.Value));

        // What birth and every later connect do.
        public Task<UnitDeployResult> Born() => ShippedUnits.Deploy(Studio, App, Alias, "Circle room");
    }

    // The creator, at birth and when answering the approval.
    private static IDisposable AsCreator(INeuronRuntime runtime) => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value));

    [Fact]
    public async Task AShippedUnitWaitsForThePersonsConsent()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var creator = AsCreator(runtime);
        var brain = new Brain(runtime);

        var result = await brain.Born();

        Assert.True(result.Ok, result.Text);
        Assert.Equal(DigitalBrain.Units.InstallStatus.Pending, result.Installs[Alias].Status);
        Assert.Null(await brain.Installs.Describe(Alias, Ct));
        var ask = Assert.Single(await brain.Installs.Pending(Ct), pending => pending.Alias == Alias);
        // The card the creator sees in Approvals reads as a synapse consent, with the needs sentence.
        Assert.Contains(await brain.Approvals.Pending(Ct), approval => approval.PlanId == ask.PlanId && approval.Kind == ApprovalKind.SynapseStaged && approval.Subject == Alias
            && approval.Detail.Contains("show a view", StringComparison.Ordinal));
        Assert.Contains((await brain.Inbox.Read()).Pending, pending => pending.PlanId == ask.PlanId);
        // The unit's app record says what it waits on.
        Assert.Contains((await brain.App.Read()).Activity, line => line.Contains("show a view", StringComparison.Ordinal));
        // No one has answered: the journal holds no resolution.
        Assert.Empty(await runtime.Signals<ConfirmationResolved>(brain.Confirmations, Ct));
    }

    [Fact]
    public async Task ApprovingTheRoomInstallsIt()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var creator = AsCreator(runtime);
        var brain = new Brain(runtime);
        await brain.Born();
        var ask = Assert.Single(await brain.Installs.Pending(Ct), pending => pending.Alias == Alias);

        // The person answers the card in their inbox.
        await brain.Inbox.Resolve(ask.PlanId, true);

        for (var i = 0; i < 100 && await brain.Installs.Describe(Alias, Ct) is null; i++) { await Task.Delay(50, Ct); }
        Assert.NotNull(await brain.Installs.Describe(Alias, Ct));
        // The room compiles inside the synapse allow-list: no diagnostics, none of them DB0045.
        var check = await brain.Studio.Check(ShippedUnits.Files(Alias)["CircleRoom.cs"]);
        Assert.True(check.Ok, check.Text);
        Assert.DoesNotContain("DB0045", check.Text, StringComparison.Ordinal);
        // Installed, the room's state answers as its own view reader (a view appears with its first state, as the unit's scenario
        // fact shows: CircleRoomUnitFacts.AJoinThenAMessageShowsBothInTheView).
        Assert.Null(await runtime.Get<IViewReader>(new NeuronId(runtime.Brain, Alias, "default")).Read(Alias));
        Assert.Empty(await brain.Installs.Pending(Ct));
        // The next connect finds the unit installed and finishes the app's build.
        await brain.Born();
        Assert.Equal(AppStatus.Live, (await brain.App.Read()).Status);
    }

    [Fact]
    public async Task AConnectAfterACrashFinishesTheRoomBuild()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var creator = AsCreator(runtime);
        var brain = new Brain(runtime);
        // A birth that crashed after the record was created and its build begun, before anything deployed.
        await brain.App.Create(new AppManifest("Circle room", "Circle room", [], [], [], Headless: true), "");
        await brain.App.BeginBuild("shipped unit " + Alias);

        var result = await brain.Born();

        Assert.True(result.Ok, result.Text);
        var ask = Assert.Single(await brain.Installs.Pending(Ct), pending => pending.Alias == Alias);
        var app = await brain.App.Read();
        Assert.Equal(AppStatus.Building, app.Status);
        Assert.StartsWith("waiting for approval: ", app.Activity[^1], StringComparison.Ordinal);

        await brain.Inbox.Resolve(ask.PlanId, true);
        for (var i = 0; i < 100 && await brain.Installs.Describe(Alias, Ct) is null; i++) { await Task.Delay(50, Ct); }
        await brain.Born();
        Assert.Equal(AppStatus.Live, (await brain.App.Read()).Status);
    }

    [Fact]
    public async Task AConnectRetryNeverApproves()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var creator = AsCreator(runtime);
        var brain = new Brain(runtime);
        await brain.Born();
        var ask = Assert.Single(await brain.Installs.Pending(Ct), pending => pending.Alias == Alias);

        var again = await brain.Born();
        await brain.Born();

        Assert.True(again.Ok, again.Text);
        Assert.Single(await brain.Studio.List());
        Assert.Equal(ask.PlanId, Assert.Single(await brain.Installs.Pending(Ct), pending => pending.Alias == Alias).PlanId);
        Assert.Null(await brain.Installs.Describe(Alias, Ct));
        Assert.Empty(await runtime.Signals<ConfirmationResolved>(brain.Confirmations, Ct));
        Assert.Equal(AppStatus.Building, (await brain.App.Read()).Status);
    }
}
