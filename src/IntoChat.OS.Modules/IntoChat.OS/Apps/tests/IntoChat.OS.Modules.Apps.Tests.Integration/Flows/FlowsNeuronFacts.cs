using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Flows;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// The flows index on a real silo: it folds the brain's flow.* signals into views and drives flows by alias.
public sealed class FlowsNeuronFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FlowsFixture : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.TryAddSingleton(TimeProvider.System);
        }
    }

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static string Inner(TestInner inner) => JsonSerializer.Serialize(inner, TestFlow.Json);

    private static async Task<FlowView> Eventually(IFlows flows, string flowId, Func<FlowView, bool> done)
    {
        FlowView? view = null;
        for (var i = 0; i < 200; i++)
        {
            view = await flows.Read(flowId, Ct);
            if (view is not null && done(view)) { return view; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"The flow {flowId} did not reach the expected view; last: {view}");
        return view!;
    }

    [Fact]
    public async Task FoldsStepSignalsIntoView()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        var id = await flows.Start(TestFlow.Alias, "Fold", null, Inner(new TestInner { Wait = true }), Ct);

        var view = await Eventually(flows, id, v => v.Status == FlowStatus.Waiting);
        Assert.Equal(["first", "second", "third"], view.Steps);
        Assert.Equal(["first"], view.Done);
        Assert.Equal(1d / 3, view.Fraction, 6);
        Assert.Equal("Fold", view.Title);
        Assert.Equal(TestFlow.Alias, view.Type);
        Assert.Equal("system", view.Owner);
    }

    [Fact]
    public async Task WaitingShowsReason()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        var id = await flows.Start(TestFlow.Alias, "Wait", null, Inner(new TestInner { Wait = true }), Ct);
        var waiting = await Eventually(flows, id, v => v.Status == FlowStatus.Waiting);
        Assert.Equal(WaitReason.External, waiting.WaitingFor);
        Assert.Equal("second", waiting.WaitingStep);

        await flows.Resume(id, "second", WaitOutcome.Arrived, Ct);
        var done = await Eventually(flows, id, v => v.Status == FlowStatus.Completed);
        Assert.Null(done.WaitingFor);
        Assert.Equal(1d, done.Fraction, 6);
    }

    [Fact]
    public async Task BlockedWhenBlockedByTargetOpen()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);
        var gate = Guid.NewGuid().ToString("N");

        var (blocked, target, resumed) = await BlockedPair(flows, gate);
        try
        {
            var view = await Eventually(flows, blocked, v => v.Status == FlowStatus.Blocked);
            Assert.Equal([target], view.BlockedBy);
        }
        finally
        {
            TestFlow.Gate(gate).TrySetResult();
            await resumed;
        }

        await flows.Resume(target, "second", WaitOutcome.Arrived, Ct);
        await Eventually(flows, blocked, v => v.Status == FlowStatus.Completed);
    }

    [Fact]
    public async Task StartWithExistingKeyIsRefused()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        var id = await flows.Start(TestFlow.Alias, "Keyed", null, Inner(new TestInner()), Ct, "keyed");
        var refused = await Assert.ThrowsAsync<DigitalBrain.Rules.RuleViolationException>(() => flows.Start(TestFlow.Alias, "Again", null, Inner(new TestInner()), Ct, "keyed"));

        Assert.Equal(runtime.Brain.Value + "/" + TestFlow.Alias + "/keyed", id);
        Assert.Contains($"flow exists: {id}", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartRefusedWhileBlocked()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);
        var gate = Guid.NewGuid().ToString("N");

        var (blocked, target, resumed) = await BlockedPair(flows, gate);
        try
        {
            await Eventually(flows, blocked, v => v.Status == FlowStatus.Blocked);
            var refused = await Assert.ThrowsAsync<DigitalBrain.Rules.RuleViolationException>(() => flows.Start(TestFlow.Alias, "Child", blocked, Inner(new TestInner()), Ct));
            Assert.Equal($"blocked by {target}", refused.Message);
        }
        finally
        {
            TestFlow.Gate(gate).TrySetResult();
            await resumed;
        }
    }

    [Fact]
    public async Task CancelCancelsLeafOperation()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        var id = await flows.Start(TestFlow.Alias, "Leaf", null, Inner(new TestInner { Wait = true, StartOperation = true }), Ct);
        await Eventually(flows, id, v => v.Status == FlowStatus.Waiting);
        var starter = runtime.Get<IFlowStarter>(NeuronId.Parse(id));
        var inner = JsonSerializer.Deserialize<TestInner>((await starter.Head()).InnerJson, TestFlow.Json)!;
        Assert.NotNull(inner.OperationId);

        await flows.Cancel(id, "stop", Ct);

        Assert.True(TestFlow.Operations[inner.OperationId].Cancellation.IsCancellationRequested);
        var view = await Eventually(flows, id, v => v.Status == FlowStatus.Cancelled);
        Assert.Equal("stop", view.Detail);
    }

    [Fact]
    public async Task ListFiltersByStatusAndLimit()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        string[] waiting = [
            await flows.Start(TestFlow.Alias, "W1", null, Inner(new TestInner { Wait = true }), Ct),
            await flows.Start(TestFlow.Alias, "W2", null, Inner(new TestInner { Wait = true }), Ct),
            await flows.Start(TestFlow.Alias, "W3", null, Inner(new TestInner { Wait = true }), Ct)];
        var done = await flows.Start(TestFlow.Alias, "Done", null, Inner(new TestInner()), Ct);
        foreach (var id in waiting) { await Eventually(flows, id, v => v.Status == FlowStatus.Waiting); }
        await Eventually(flows, done, v => v.Status == FlowStatus.Completed);

        var two = await flows.List(new FlowQuery(null, FlowStatus.Waiting, null, null, Limit: 2), Ct);
        var completed = await flows.List(new FlowQuery(null, FlowStatus.Completed, TestFlow.Alias, null), Ct);
        var none = await flows.List(new FlowQuery(null, null, "other.flow", null), Ct);

        Assert.Equal(2, two.Length);
        Assert.All(two, view => Assert.Equal(FlowStatus.Waiting, view.Status));
        Assert.Equal([done], completed.Select(view => view.FlowId));
        Assert.Empty(none);
    }

    // The flow ran before the index existed, so its own signals were never delivered; the pump then sends its done first, a
    // repeated step and a late start. The done-time head read fills in the real steps and owner.
    [Fact]
    public async Task DuplicateAndOutOfOrderSignalsFoldOnce()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var key = runtime.Id(TestFlow.Alias, Guid.NewGuid().ToString("N"));
        var id = key.ToString();
        await runtime.Get<IFlowStarter>(key).StartFromJson("Late", "owner-1", null, Inner(new TestInner()));
        var flows = Flows(runtime);
        await flows.List(new FlowQuery(), Ct);
        var pump = runtime.Get<IFlowSignalPump>(runtime.Id(FlowSignalPump.Alias, "pump"));

        await pump.Done(id);
        await pump.StepCompleted(id, "first");
        await pump.StepCompleted(id, "first");
        await pump.Started(id, TestFlow.Alias, "Late");

        var view = await Eventually(flows, id, v => v.Title == "Late" && v.Done.Length > 0);
        Assert.Equal(FlowStatus.Completed, view.Status);
        Assert.Equal(["first", "second", "third"], view.Done);
        Assert.Equal(["first", "second", "third"], view.Steps);
        Assert.Equal("owner-1", view.Owner);
        Assert.NotEmpty(view.TraceId);
        Assert.NotNull(view.Ended);
    }

    [Fact]
    public async Task HeadReadStopsAfterThreeFailures()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);
        await flows.List(new FlowQuery(), Ct);
        var pump = runtime.Get<IFlowSignalPump>(runtime.Id(FlowSignalPump.Alias, "pump"));
        var id = runtime.Id(FailingHeadFlow.Alias, Guid.NewGuid().ToString("N")).ToString();

        foreach (var step in new[] { "a", "b", "c", "d" }) { await pump.StepCompleted(id, step); }
        await Eventually(flows, id, v => v.Done.Length == 4);
        await flows.List(new FlowQuery(), Ct);
        await flows.Read(id, Ct);

        Assert.Equal(3, FailingHeadFlow.Reads[id]);
        Assert.Empty((await flows.Read(id, Ct))!.Steps);
    }

    [Fact]
    public async Task ResolveBeforeWaitingDoesNotLeaveWaiting()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);
        await flows.List(new FlowQuery(), Ct);
        var pump = runtime.Get<IFlowSignalPump>(runtime.Id(FlowSignalPump.Alias, "pump"));
        var id = runtime.Id(TestFlow.Alias, Guid.NewGuid().ToString("N")).ToString();

        await pump.Started(id, TestFlow.Alias, "Early");
        await pump.Resolved(id, "second");
        await pump.Waiting(id, "second");
        await pump.StepCompleted(id, "first");

        var view = await Eventually(flows, id, v => v.Done.Contains("first"));
        Assert.NotEqual(FlowStatus.Waiting, view.Status);
        Assert.Null(view.WaitingFor);
    }

    // A FlowWaiting for a step whose wait was already resolved, redelivered after a later step started, must not re-open the wait.
    [Fact]
    public async Task StaleWaitingAfterLaterStepIsIgnored()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);
        await flows.List(new FlowQuery(), Ct);
        var pump = runtime.Get<IFlowSignalPump>(runtime.Id(FlowSignalPump.Alias, "pump"));
        var id = runtime.Id(TestFlow.Alias, Guid.NewGuid().ToString("N")).ToString();

        await pump.Started(id, TestFlow.Alias, "Reordered");
        await pump.Waiting(id, "second");
        await pump.Resolved(id, "second");
        await pump.StepStarted(id, "third");
        await pump.Waiting(id, "second");
        await pump.StepCompleted(id, "first");

        var view = await Eventually(flows, id, v => v.Done.Contains("first"));
        Assert.Equal(FlowStatus.Running, view.Status);
        Assert.Equal("third", view.Current);
        Assert.Null(view.WaitingFor);
        Assert.Null(view.WaitingStep);
    }

    [Fact]
    public void EvictionKeepsLast2000Finished()
    {
        var start = DateTimeOffset.UnixEpoch;
        FlowEntry Entry(string id, FlowStatus status, int minute)
            => new(id, TestFlow.Alias, id, "system", "", status, [], [], null, null, [], null, start, status == FlowStatus.Running ? null : start.AddMinutes(minute), null);
        var flows = new Dictionary<string, FlowEntry>(StringComparer.Ordinal) { ["running"] = Entry("running", FlowStatus.Running, 0) };
        for (var i = 0; i < FlowsState.KeepFinished; i++) { flows[$"done-{i}"] = Entry($"done-{i}", FlowStatus.Completed, i + 1); }

        var next = new FlowsState(flows).With(Entry("newest", FlowStatus.Cancelled, FlowsState.KeepFinished + 1));

        Assert.Equal(FlowsState.KeepFinished, next.Flows.Values.Count(entry => entry.Finished));
        Assert.DoesNotContain("done-0", next.Flows.Keys);
        Assert.Contains("done-1", next.Flows.Keys);
        Assert.Contains("newest", next.Flows.Keys);
        Assert.Contains("running", next.Flows.Keys);
    }

    [Fact]
    public async Task UnknownBlockedByTargetDoesNotBlock()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);
        var gate = Guid.NewGuid().ToString("N");
        var gone = runtime.Id(TestFlow.Alias, "evicted").ToString();

        var id = await flows.Start(TestFlow.Alias, "Free", null, Inner(new TestInner { Wait = true, Gate = gate }), Ct);
        await Eventually(flows, id, v => v.Status == FlowStatus.Waiting);
        await flows.Relate(id, new FlowRelation(gone, RelationKind.BlockedBy, null), Ct);
        await Eventually(flows, id, v => v.Relations.Length == 1);
        var resumed = flows.Resume(id, "second", WaitOutcome.Arrived, Ct);
        try
        {
            var view = await Eventually(flows, id, v => v.Current == "third");
            Assert.Equal(FlowStatus.Running, view.Status);
            Assert.Empty(view.BlockedBy);
        }
        finally
        {
            TestFlow.Gate(gate).TrySetResult();
            await resumed;
        }
    }

    // The supported pattern: a step starts the leaf operation, stores its id and waits External for OperationDone.
    [Fact]
    public async Task FractionIncludesLeafOperation()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        var id = await flows.Start(TestFlow.Alias, "Leaf", null, Inner(new TestInner { Wait = true, StartOperation = true }), Ct);

        var view = await Eventually(flows, id, v => v.Status == FlowStatus.Waiting && Math.Abs(v.Fraction - (1 + 0.5) / 3) < 1e-6);
        Assert.Equal(["first"], view.Done);
        Assert.Equal("second", view.Current);
    }

    [Fact]
    public async Task StartRecordsCallerAppAndConversation()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        string id;
        using (RuntimeCallerContext.Stamp(CapabilityCallers.Of("person-1", "account-1", runtime.Brain.Value, CallerKind.Agent, conversationId: "ino/conversations/main")))
        {
            id = await flows.Start(TestFlow.Alias, "By Ino", null, Inner(new TestInner { Wait = true }), Ct);
        }

        var view = await Eventually(flows, id, view => view.Status == FlowStatus.Waiting && view.App is not null);
        Assert.Equal("person-1", view.Owner);
        Assert.Equal("assistant", view.App);
        Assert.Equal("ino/conversations/main", view.Conversation);
    }

    [Fact]
    public async Task CallerBecomesOwner()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        string id;
        using (RuntimeCallerContext.Stamp(new CapabilityCaller("person-1", "account-1", runtime.Brain.Value)))
        {
            id = await flows.Start(TestFlow.Alias, "Mine", null, Inner(new TestInner { Wait = true }), Ct);
        }

        var view = await Eventually(flows, id, v => v.Status == FlowStatus.Waiting);
        Assert.Equal("person-1", view.Owner);
    }

    [Fact]
    public async Task CancelAfterOperationEndedDoesNotCancelAgain()
    {
        await using var fixture = new FlowsFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Flows(runtime);

        var id = await flows.Start(TestFlow.Alias, "Ended", null, Inner(new TestInner { Wait = true, StartOperation = true }), Ct);
        await Eventually(flows, id, v => v.Status == FlowStatus.Waiting);
        var operationId = (await runtime.Get<IFlowStarter>(NeuronId.Parse(id)).Head()).OperationId!;
        var operation = TestFlow.Operations[operationId];
        await operation.Complete(new OperationResult.Succeeded());

        await flows.Cancel(id, "stop", Ct);

        Assert.False(operation.Cancellation.IsCancellationRequested);
        await Eventually(flows, id, v => v.Status == FlowStatus.Cancelled);
    }

    // A flow waiting at "second" is related BlockedBy a target that waits too, then resumed into "third", which holds on the gate,
    // so the blocked flow is Running while its target is open.
    private static async Task<(string Blocked, string Target, Task Resumed)> BlockedPair(IFlows flows, string gate)
    {
        var target = await flows.Start(TestFlow.Alias, "Target", null, Inner(new TestInner { Wait = true }), Ct);
        var blocked = await flows.Start(TestFlow.Alias, "Blocked", null, Inner(new TestInner { Wait = true, Gate = gate }), Ct);
        await Eventually(flows, target, v => v.Status == FlowStatus.Waiting);
        await Eventually(flows, blocked, v => v.Status == FlowStatus.Waiting);
        await flows.Relate(blocked, new FlowRelation(target, RelationKind.BlockedBy, null), Ct);
        await Eventually(flows, blocked, v => v.Relations.Length == 1);
        var resumed = flows.Resume(blocked, "second", WaitOutcome.Arrived, Ct);
        await Eventually(flows, blocked, v => v.Current == "third");
        return (blocked, target, resumed);
    }
}
