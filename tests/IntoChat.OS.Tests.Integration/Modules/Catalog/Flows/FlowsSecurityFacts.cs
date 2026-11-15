using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// A trusted security interrupt on a real silo: it refuses Ino flow starts, and with the cancel-flows tag cancels Ino flows that run.
public sealed class FlowsSecurityFacts
{
    private const string Conversation = "ino/conversations/main";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class SecurityFixture : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(AIModule), typeof(FlowsFactsModule)];


        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.AddJournalPayloads(typeof(ContextUpdated).Assembly);
            services.TryAddSingleton(TimeProvider.System);
        }
    }

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static IAgentContext Posture(INeuronRuntime runtime)
        => runtime.Get<IAgentContext>(new NeuronId(runtime.Brain, "agent-context", "context/security.posture"));

    private static string Inner() => JsonSerializer.Serialize(new TestInner { Wait = true }, TestFlow.Json);

    private static IDisposable As(INeuronRuntime runtime, CallerKind kind)
        => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value, kind));

    // An agent's tool call as ContractActions.Marked stamps it: AppId "assistant" and the conversation.
    private static IDisposable AsInoTool(INeuronRuntime runtime)
        => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value, CallerKind.Agent, conversationId: Conversation));

    private static bool IsRefusal(Exception error) => error.ToString().Contains("security interrupt", StringComparison.Ordinal);

    private static async Task<string> StartInoFlow(INeuronRuntime runtime, string title)
    {
        string id;
        using (AsInoTool(runtime)) { id = await Flows(runtime).Start(TestFlow.Alias, title, null, Inner(), Ct); }
        await Eventually(runtime, id, view => view.Status == FlowStatus.Waiting);
        return id;
    }

    private static async Task<FlowView> Eventually(INeuronRuntime runtime, string flowId, Func<FlowView, bool> done)
    {
        FlowView? view = null;
        for (var i = 0; i < 200; i++)
        {
            view = await Flows(runtime).Read(flowId, Ct);
            if (view is not null && done(view)) { return view; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"The flow {flowId} did not reach the expected view; last: {view}");
        return view!;
    }

    // Tries an Ino start until the index has folded the interrupt, and returns the refusal.
    private static async Task<Exception> RefusedEventually(INeuronRuntime runtime)
    {
        for (var i = 0; i < 200; i++)
        {
            try
            {
                string id;
                using (AsInoTool(runtime)) { id = await Flows(runtime).Start(TestFlow.Alias, "Probe", null, Inner(), Ct); }
                await Flows(runtime).Cancel(id, "probe", Ct);
            }
            catch (Exception error) when (IsRefusal(error)) { return error; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail("The Ino start was not refused.");
        return null!;
    }

    [Fact]
    public async Task StartRefusedUnderTrustedInterrupt()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);

        using (As(runtime, CallerKind.User)) { await Posture(runtime).Set(new ContextEntry("stop", "Hold everything.", [], [ContextTags.Interrupt])); }

        var refusal = await RefusedEventually(runtime);
        Assert.Contains("blocked by security interrupt: Hold everything.", refusal.ToString(), StringComparison.Ordinal);

        // A person's or the system's start is not refused.
        using (As(runtime, CallerKind.User)) { Assert.NotNull(await Flows(runtime).Start(TestFlow.Alias, "By person", null, Inner(), Ct)); }
        Assert.NotNull(await Flows(runtime).Start(TestFlow.Alias, "By system", null, Inner(), Ct));

        // Removing the interrupt lifts the refusal.
        using (As(runtime, CallerKind.Platform)) { await Posture(runtime).Remove("stop"); }
        for (var i = 0; i < 200; i++)
        {
            try
            {
                using (AsInoTool(runtime)) { await Flows(runtime).Start(TestFlow.Alias, "After", null, Inner(), Ct); }
                return;
            }
            catch (Exception error) when (IsRefusal(error)) { await Task.Delay(50, Ct); }
        }
        Assert.Fail("The interrupt was removed but Ino starts stay refused.");
    }

    [Fact]
    public async Task ModelTrustInterruptDoesNotRefuse()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);
        var running = await StartInoFlow(runtime, "Running");

        // The model's entry is stored but not acted on. A user's marker written after it is folded after it, so once the marker
        // refuses a start the model's entry has been folded too; the refusal names only the marker.
        using (As(runtime, CallerKind.Agent)) { await Posture(runtime).Set(new ContextEntry("stop", "Model says stop.", [], [ContextTags.Interrupt, "cancel-flows"])); }
        using (As(runtime, CallerKind.User)) { await Posture(runtime).Set(new ContextEntry("marker", "User marker.", [], [ContextTags.Interrupt])); }
        var refusal = await RefusedEventually(runtime);
        Assert.Contains("blocked by security interrupt: User marker.", refusal.ToString(), StringComparison.Ordinal);
        Assert.Equal(FlowStatus.Waiting, (await Flows(runtime).Read(running, Ct))!.Status);

        // With the marker gone nothing holds: the model's entry never counted.
        using (As(runtime, CallerKind.Platform)) { await Posture(runtime).Remove("marker"); }
        string started = "";
        for (var i = 0; i < 200 && started.Length == 0; i++)
        {
            try { started = await StartInoFlow(runtime, "Still allowed"); }
            catch (Exception error) when (IsRefusal(error)) { await Task.Delay(50, Ct); }
        }
        Assert.NotEqual("", started);
        Assert.NotEqual(running, started);
        Assert.Equal(FlowStatus.Waiting, (await Flows(runtime).Read(running, Ct))!.Status);
    }

    [Fact]
    public async Task CancelFlowsTagCancelsRunningInoFlows()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);
        var ino = await StartInoFlow(runtime, "Ino flow");
        string person;
        using (As(runtime, CallerKind.User)) { person = await Flows(runtime).Start(TestFlow.Alias, "Person flow", null, Inner(), Ct); }
        await Eventually(runtime, person, view => view.Status == FlowStatus.Waiting);

        using (As(runtime, CallerKind.User)) { await Posture(runtime).Set(new ContextEntry("stop", "Stop and cancel.", [], [ContextTags.Interrupt, "cancel-flows"])); }

        var cancelled = await Eventually(runtime, ino, view => view.Status == FlowStatus.Cancelled);
        Assert.Equal("security interrupt", cancelled.Detail);
        Assert.Equal(FlowStatus.Waiting, (await Flows(runtime).Read(person, Ct))!.Status);
    }

    // The interrupt is written with the person's trust, so it ends an Ino run that waits for the person too; the plan leaves the inbox.
    [Fact]
    public async Task CancelFlowsTagCancelsInoFlowWaitingForApproval()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);
        var inbox = runtime.Get<IConfirmationInbox>(new NeuronId(runtime.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        string ino;
        using (AsInoTool(runtime)) { ino = await Flows(runtime).Start(TestFlow.Alias, "Ino flow", null, JsonSerializer.Serialize(new TestInner { WaitApproval = true }, TestFlow.Json), Ct); }
        var waiting = await Eventually(runtime, ino, view => view.Status == FlowStatus.Waiting && view.WaitingFor == WaitReason.Approval && view.ConfirmationPlanId is not null);
        for (var i = 0; i < 200 && (await inbox.Read()).Pending.All(plan => plan.PlanId != waiting.ConfirmationPlanId); i++) { await Task.Delay(50, Ct); }
        Assert.Contains((await inbox.Read()).Pending, plan => plan.PlanId == waiting.ConfirmationPlanId);

        using (As(runtime, CallerKind.User)) { await Posture(runtime).Set(new ContextEntry("stop", "Stop and cancel.", [], [ContextTags.Interrupt, "cancel-flows"])); }

        var cancelled = await Eventually(runtime, ino, view => view.Status == FlowStatus.Cancelled);
        Assert.Equal("security interrupt", cancelled.Detail);
        for (var i = 0; i < 200 && (await inbox.Read()).Pending.Length > 0; i++) { await Task.Delay(50, Ct); }
        Assert.Empty((await inbox.Read()).Pending);
    }

    [Fact]
    public async Task PlainInterruptLeavesRunningFlowsToPauseAtNextWait()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);
        var ino = await StartInoFlow(runtime, "Ino flow");

        using (As(runtime, CallerKind.User)) { await Posture(runtime).Set(new ContextEntry("stop", "Just pause.", [], [ContextTags.Interrupt])); }
        await RefusedEventually(runtime);
        await Flows(runtime).List(new FlowQuery(), Ct); // queues behind the handler that folded the entry

        var view = (await Flows(runtime).Read(ino, Ct))!;
        Assert.Equal(FlowStatus.Waiting, view.Status);
        Assert.Null(view.Detail);
    }
}
