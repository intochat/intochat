using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.Apps.Approvals;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using Xunit;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Flows.FlowTypeFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class PlanFlowFacts
{
    private const string Plan = """
        Here is the plan:
        { "subtasks": [
          { "title": "Write the notes", "type": "flow.note", "inner": { "text": "notes" } },
          { "title": "Review the notes", "type": "flow.note", "inner": {}, "relations": [ { "target": 0, "kind": "BlockedBy", "lag": "1.00:00:00" } ] } ] }
        """;

    [Fact]
    public async Task PlanDraftsWaitsAndStartsSubtasksOnApprove()
    {
        ScriptedAgent.Replies["Plan"] = Plan;
        await using var fixture = new FlowTypesFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Index(runtime);

        var id = await flows.Start(PlanFlow.Alias, "Notes", null, JsonSerializer.Serialize(new PlanInner { Goal = "Keep notes" }, PlanFlow.Json), Ct);

        var waiting = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Waiting && v.WaitingStep == "approve");
        Assert.Equal(["draft"], waiting.Done);
        Assert.Equal(WaitReason.Approval, waiting.WaitingFor);
        Assert.Contains("Keep notes", ScriptedAgent.Prompts[id], StringComparison.Ordinal);
        Assert.Empty(waiting.Relations);

        // An approval wait is resolved by the person through the confirmations inbox, as ApprovalsFacts does it.
        await runtime.ApproveAsPerson(await PendingPlan(runtime, id), Ct);

        // The index can show Completed from the flow's head before it folds the FlowRelated signals saved before it.
        var done = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Completed && v.Relations.Length == 2);
        Assert.Equal(["draft", "approve", "start-subtasks"], done.Done);
        var inner = JsonSerializer.Deserialize<PlanInner>((await runtime.Get<IFlowStarter>(Id(id)).Head()).InnerJson, PlanFlow.Json)!;
        Assert.Equal(2, inner.Started.Length);
        Assert.Equal(inner.Started.Select(child => new FlowRelation(child, RelationKind.Subtask, null)), done.Relations);
        var first = await Eventually(() => flows.Read(inner.Started[0], Ct), v => v.Status == FlowStatus.Waiting);
        Assert.Equal(("Write the notes", id), (first.Title, first.ParentFlowId));
        var second = await Eventually(() => flows.Read(inner.Started[1], Ct), v => v.Relations.Length == 1);
        Assert.Equal(new FlowRelation(inner.Started[0], RelationKind.BlockedBy, TimeSpan.FromDays(1)), second.Relations[0]);
    
    }

    private static async Task<string> PendingPlan(INeuronRuntime runtime, string flowId)
    {
        var approvals = runtime.Get<IApprovals>(IApprovals.Key(runtime.Brain.Value));
        for (var i = 0; i < 200; i++)
        {
            using (RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value)))
            {
                if ((await approvals.Pending(Ct)).FirstOrDefault(a => a.Kind == ApprovalKind.FlowStep) is { } pending) { return pending.PlanId; }
            }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"Flow {flowId} raised no pending approval.");
        return "";
    }

    [Theory]
    [InlineData("""{ "subtasks": [ { "title": "A", "type": "flow.note" }, { "title": "B", "type": "flow.note", "relations": [ { "target": "0", "kind": "BlockedBy" } ] } ] }""")]
    [InlineData("""{ "subtasks": [ { "title": "A", "type": "flow.note" }, { "title": "B", "type": "flow.note", "relations": [ 5 ] } ] }""")]
    [InlineData("""{ "subtasks": [ { "title": 3, "type": "flow.note" } ] }""")]
    public async Task MalformedPlanFailsTheFlowCleanly(string plan)
    {
        var goal = "Malformed " + Guid.NewGuid().ToString("N");
        ScriptedAgent.Replies["Plan this goal as subtasks: " + goal] = plan;
        await using var fixture = new FlowTypesFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Index(runtime);

        var id = await flows.Start(PlanFlow.Alias, "Malformed", null, JsonSerializer.Serialize(new PlanInner { Goal = goal }, PlanFlow.Json), Ct);

        var view = await Eventually(() => flows.Read(id, Ct), v => v is { Status: FlowStatus.Failed, Detail: not null } or { Status: FlowStatus.Completed } || v.WaitingStep == "approve");
        Assert.Equal(FlowStatus.Failed, view.Status);
        Assert.StartsWith("plan:", view.Detail, StringComparison.Ordinal);
        Assert.Contains(await runtime.Signals<FlowDone>(Id(id), Ct), done => done is { Outcome: FlowOutcome.Failed } && done.Detail!.StartsWith("plan:", StringComparison.Ordinal));
    }
}
