using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Confirmation;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition.Confirmations;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.Confirmations;

public sealed class ConfirmationRuntimeFacts
{
    private const string Brain = "ada";

    [Fact]
    public async Task PersonApprovesWithinExpiry()
    {
        var (neuron, context) = await Start();
        await neuron.Begin(Plan("p-person"), "app.files");
        using (Person()) { await neuron.Resolve("p-person", true); }

        var result = await neuron.Result("p-person");
        Assert.Equal((ConfirmationOutcome.Approved, ConfirmationDenyReason.None), (result!.Outcome, result.Reason));
        Assert.Equal(PrincipalKind.Person, Audit(context).Approver!.Kind);
    }

    [Fact]
    public async Task AgentResolveIsSelfApproval()
    {
        var (neuron, context) = await Start();
        await neuron.Begin(Plan("p-agent"), "app.files");
        using (Stamp(CapabilityCallers.Of("agent", "ada", Brain, CallerKind.Assistant))) { await neuron.Resolve("p-agent", true); }

        var result = await neuron.Result("p-agent");
        Assert.Equal((ConfirmationOutcome.Denied, ConfirmationDenyReason.SelfApproval), (result!.Outcome, result.Reason));
        Assert.Equal(PrincipalKind.Agent, Audit(context).Approver!.Kind);
    }

    [Fact]
    public async Task AppResolveIsSelfApproval()
    {
        var (neuron, context) = await Start();
        await neuron.Begin(Plan("p-app"), "app.files");
        using (Stamp(CapabilityCallers.Of("app.files", "ada", Brain, CallerKind.App, "app.files"))) { await neuron.Resolve("p-app", true); }

        var result = await neuron.Result("p-app");
        Assert.Equal(ConfirmationDenyReason.SelfApproval, result!.Reason);
        Assert.Equal(PrincipalKind.App, Audit(context).Approver!.Kind);
    }

    [Fact]
    public async Task UnstampedResolveIsRejected()
    {
        var (neuron, _) = await Start();
        await neuron.Begin(Plan("p-open"), "app.files");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => neuron.Resolve("p-open", true));
        Assert.Null(await neuron.Result("p-open"));
    }

    [Fact]
    public async Task ExpiryWithoutResolve()
    {
        var (neuron, context) = await Start();
        await neuron.Begin(Plan("p-expire"), "app.files");

        await context.Timers.Advance(TimeSpan.FromMinutes(5));

        var result = await neuron.Result("p-expire");
        Assert.Equal((ConfirmationOutcome.Expired, ConfirmationDenyReason.Expired), (result!.Outcome, result.Reason));
        Assert.Null(Audit(context).Approver);
    }

    [Fact]
    public async Task PolicyNeverSkips()
    {
        var (neuron, context) = await Start(ConfirmationPolicy.Never);
        await neuron.Begin(Plan("p-never"), "app.files");

        var result = await neuron.Result("p-never");
        Assert.True(result!.Approved);
        Assert.True(result.Skipped);
        var audit = Audit(context);
        Assert.True(audit.Skipped);
        Assert.Null(audit.Approver);
        Assert.DoesNotContain("\\", audit.TargetLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartKeepsThePendingPlan()
    {
        var (neuron, context) = await Start();
        await neuron.Begin(Plan("p-restart"), "app.files");
        await NeuronAttachment.Deactivate(neuron, DeactivationCause.Requested, TestContext.Current.CancellationToken);
        var carried = (await context.MemoryState.Load<ConfirmationState>())!;

        var again = new FakeNeuronContext(context.Id);
        await again.MemoryState.Save(carried, context.MemoryState.Signals);
        var restarted = new ConfirmationsNeuron(Options.Create(new ConfirmationOptions()));
        again.Timers.OnFire = name => NeuronAttachment.Remind(restarted, name, CancellationToken.None);
        NeuronAttachment.Attach(restarted, again);
        await NeuronAttachment.Activate(restarted, TestContext.Current.CancellationToken);

        Assert.Null(await restarted.Result("p-restart"));
        using (Person()) { await restarted.Resolve("p-restart", true); }
        Assert.Equal(ConfirmationOutcome.Approved, (await restarted.Result("p-restart"))!.Outcome);
    }

    [Fact]
    public async Task PersonClickThroughConfirmViewApproves()
    {
        var (neuron, context) = await Start();
        var plan = Plan("p-click");
        await neuron.Begin(plan, "app.files");
        var card = ConfirmView.For(plan.Id, plan.Title, plan.Body, plan.ApproveLabel, plan.DenyLabel, plan.Expiry, viewerIsHuman: true);
        var approve = card.Children.OfType<Button>().First(button => button.Action.Arguments[1] == "true");
        Assert.Equal(IConfirmations.ConfirmationsNeuronKey, approve.Action.Neuron);
        Assert.Equal(IConfirmations.ResolveOperation, approve.Action.Operation);

        using (Person()) { await neuron.Resolve(approve.Action.Arguments[0], bool.Parse(approve.Action.Arguments[1])); }

        Assert.Equal(ConfirmationOutcome.Approved, (await neuron.Result("p-click"))!.Outcome);
        Assert.Equal(PrincipalKind.Person, Audit(context).Approver!.Kind);
    }

    [Fact]
    public async Task AppRelayIsSelfApproval()
    {
        var (neuron, context) = await Start();
        await neuron.Begin(Plan("p-relay"), "app.files");
        using (Stamp(CapabilityCallers.Of("app.files", "ada", Brain, CallerKind.App, "app.files")))
        {
            await neuron.ResolveAsRequester("p-relay", true);
        }

        var result = await neuron.Result("p-relay");
        Assert.Equal((ConfirmationOutcome.Denied, ConfirmationDenyReason.SelfApproval), (result!.Outcome, result.Reason));
        Assert.Equal(PrincipalKind.App, Audit(context).Approver!.Kind);
    }

    private static ConfirmationPlan Plan(string id)
        => new(id, ConfirmationAction.Delete, true, "Delete a.txt?", "This removes a.txt from Drive. You cannot undo this.", "Delete", "Keep", "a.txt");

    private static async Task<(ConfirmationsNeuron Neuron, FakeNeuronContext Context)> Start(ConfirmationPolicy policy = ConfirmationPolicy.DestructiveOnly)
    {
        var options = new ConfirmationOptions();
        if (policy != ConfirmationPolicy.DestructiveOnly) { options.Brains[Brain] = policy; }
        var neuron = new ConfirmationsNeuron(Options.Create(options));
        var context = new FakeNeuronContext(new NeuronId(new BrainId(Brain), "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        context.Timers.OnFire = name => NeuronAttachment.Remind(neuron, name, CancellationToken.None);
        NeuronAttachment.Attach(neuron, context);
        await NeuronAttachment.Activate(neuron, TestContext.Current.CancellationToken);
        return (neuron, context);
    }

    private static IDisposable Person() => Stamp(CapabilityCallers.Of("ada", "ada", Brain));

    private static IDisposable Stamp(CapabilityCaller caller)
    {
        RequestContext.Set(KernelInvocationSink.CallerKey, "");
        return RuntimeCallerContext.Stamp(caller);
    }

    private static ConfirmationResolved Audit(FakeNeuronContext context)
        => Assert.Single(context.Published.OfType<ConfirmationResolved>());
}
