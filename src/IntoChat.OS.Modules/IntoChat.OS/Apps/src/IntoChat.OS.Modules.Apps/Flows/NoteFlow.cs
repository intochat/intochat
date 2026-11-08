using System.Text.Json;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

public sealed class NoteInner
{
    public string Text { get; set; } = "";
}

// A note: a flow with no steps that holds relations like any flow and ends only when someone ends it. Start saves it waiting on
// "note" (External, no expiry), since the base resumes only a waiting flow and advances, and so completes, a running one with no
// steps; Resume("note", Arrived) completes it, Cancel cancels it.
[DigitalBrain.Neuron(Alias)]
public sealed class NoteFlow : Flow<NoteInner>, IFlowStarter
{
    public const string Alias = "flow.note";

    public const string Step = "note";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override IReadOnlyList<string> Steps => [];

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<NoteInner>(innerJson, Json) ?? new NoteInner());

    public override async Task Start(string title, string owner, string? parentFlowId, NoteInner inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(inner);
        if (State.Status != FlowStatus.Pending) { return; }
        var flowId = Id.ToString();
        var waiting = new Waiting(Step, WaitReason.External, null, null);
        await Save(State with
        {
            FlowId = flowId,
            Title = title,
            Owner = owner,
            TraceId = Guid.NewGuid().ToString("N"),
            Status = FlowStatus.Waiting,
            Waiting = waiting,
            Current = Step,
            ParentFlowId = parentFlowId,
            Started = Context.Time.GetUtcNow(),
            Inner = inner,
        }, [new FlowStarted(flowId, Id.Type, title), new FlowWaiting(flowId, Step, WaitReason.External, null)]);
    }

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([Step], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    protected override Task<StepResult<NoteInner>> Run(string step, NoteInner inner, CancellationToken ct) => throw new FlowFailed(step, "a note has no steps");
}
