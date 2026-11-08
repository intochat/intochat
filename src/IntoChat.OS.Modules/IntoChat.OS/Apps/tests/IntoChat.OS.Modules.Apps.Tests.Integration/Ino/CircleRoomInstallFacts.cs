using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Apps;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Ino;

// A circle brain is born with its room: the shipped circleroom unit deploys through the synapse studio on a real silo; it asks
// for no slots, and the OS approves its reactions and view, so it lands installed without asking the person.
public sealed class CircleRoomInstallFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANewCircleBrainHasTheRoomInstalled()
    {
        await using var fixture = new SynapseStudioFacts.StudioFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        // Born as its creator: the birth handler runs in the creator's turn.
        using var creator = DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value));
        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        var inbox = runtime.Get<DigitalBrain.Confirmation.IConfirmationInbox>(new NeuronId(runtime.Brain, "runtime.confirmations", "confirmations"));
        Task Approve(string plan) => inbox.Resolve(plan, true);

        foreach (var alias in ShippedApps.CircleUnits)
        {
            var result = await ShippedUnits.Deploy(studio, alias, Approve);
            Assert.True(result.Ok, result.Text);
            Assert.True(result.Installs[alias].Status is DigitalBrain.InstallStatus.Installed or DigitalBrain.InstallStatus.Unchanged, result.Installs[alias].Sentence);
        }

        var unit = Assert.Single(await studio.List());
        Assert.Equal("circleroom", unit.SourceKey);
        Assert.Contains("circleroom", unit.Aliases);
        Assert.Equal(["scenarios.json", "spec.md"], (await studio.Sidecars("circleroom")).Keys.Order(StringComparer.Ordinal));
        // Born twice (a retried Create, a later connect): the same files deploy once and stay installed.
        Assert.True((await ShippedUnits.Deploy(studio, "circleroom", Approve)).Ok);
        Assert.Single(await studio.List());
    }
}
