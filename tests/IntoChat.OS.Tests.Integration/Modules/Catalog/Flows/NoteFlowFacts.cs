using System.Text.Json;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using Xunit;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Flows.FlowTypeFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class NoteFlowFacts
{
    [Fact]
    public async Task NoteHoldsRelationsAndCompletesOnArrived()
    {
        await using var fixture = new FlowTypesFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Index(runtime);
        var note = JsonSerializer.Serialize(new NoteInner { Text = "remember" }, NoteFlow.Json);
        var other = await flows.Start(NoteFlow.Alias, "Other", null, note, Ct);

        var id = await flows.Start(NoteFlow.Alias, "Remember", null, note, Ct);
        await flows.Relate(id, new FlowRelation(other, RelationKind.Relates, null), Ct);

        var held = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Waiting && v.Relations.Length == 1);
        Assert.Equal(NoteFlow.Step, held.WaitingStep);
        await Task.Delay(300, Ct);
        Assert.Equal(FlowStatus.Waiting, (await flows.Read(id, Ct))!.Status);

        await flows.Resume(id, NoteFlow.Step, WaitOutcome.Arrived, Ct);

        var done = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Completed);
        Assert.Equal([NoteFlow.Step], done.Done);
        Assert.Equal(new FlowRelation(other, RelationKind.Relates, null), Assert.Single(done.Relations));

        await flows.Cancel(other, "not needed", Ct);
        await Eventually(() => flows.Read(other, Ct), v => v.Status == FlowStatus.Cancelled);
    }
}
