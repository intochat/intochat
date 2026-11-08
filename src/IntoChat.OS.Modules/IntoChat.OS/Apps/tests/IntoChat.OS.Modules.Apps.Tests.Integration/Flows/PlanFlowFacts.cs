using System.Text.Json;
using DigitalBrain.Flows;
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

        await flows.Resume(id, "approve", WaitOutcome.Approved, Ct);

        var done = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Completed);
        Assert.Equal(["draft", "approve", "start-subtasks"], done.Done);
        var inner = JsonSerializer.Deserialize<PlanInner>((await runtime.Get<IFlowStarter>(Id(id)).Head()).InnerJson, PlanFlow.Json)!;
        Assert.Equal(2, inner.Started.Length);
        Assert.Equal(inner.Started.Select(child => new FlowRelation(child, RelationKind.Subtask, null)), done.Relations);
        var first = await Eventually(() => flows.Read(inner.Started[0], Ct), v => v.Status == FlowStatus.Waiting);
        Assert.Equal(("Write the notes", id), (first.Title, first.ParentFlowId));
        var second = await Eventually(() => flows.Read(inner.Started[1], Ct), v => v.Relations.Length == 1);
        Assert.Equal(new FlowRelation(inner.Started[0], RelationKind.BlockedBy, TimeSpan.FromDays(1)), second.Relations[0]);
    }
}
