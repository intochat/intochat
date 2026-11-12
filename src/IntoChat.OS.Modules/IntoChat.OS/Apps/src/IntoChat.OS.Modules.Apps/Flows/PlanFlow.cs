using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

public sealed class PlanRelation
{
    public int Target { get; set; }

    public string Kind { get; set; } = nameof(RelationKind.BlockedBy);

    public string? Lag { get; set; }
}

public sealed class PlanSubtask
{
    public string Title { get; set; } = "";

    public string Type { get; set; } = NoteFlow.Alias;

    public string InnerJson { get; set; } = "{}";

    public PlanRelation[] Relations { get; set; } = [];
}

// A plan's own state: Goal is what to plan; draft fills Subtasks; start-subtasks fills Started (the subtask flow ids, by index).
public sealed class PlanInner : AgentInner
{
    public string Goal { get; set; } = "";

    public PlanSubtask[]? Subtasks { get; set; }

    public string[] Started { get; set; } = [];

    public DateTimeOffset? ApprovalExpires { get; set; }
}

// One plan: draft (the flow agent answers a JSON plan), approve (a two-day approval listing the subtasks) and start-subtasks (each
// subtask a flow of its own, a Subtask of this one, with the sibling relations the plan names). Subtask keys are {planKey}-{index},
// so a start-subtasks run again starts none twice.
[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class PlanFlow(AgentStepOptions? options = null) : AgentFlow<PlanInner>(options), IFlowStarter
{
    public const string Alias = "flow.plan";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Names = ["draft", "approve", "start-subtasks"];

    private static readonly string[] Types = [BuildFlow.Alias, NoteFlow.Alias];

    private static readonly TimeSpan ApprovalWindow = TimeSpan.FromDays(2);

    protected override IReadOnlyList<string> Steps => Names;

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<PlanInner>(innerJson, Json) ?? new PlanInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Names], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    protected override async Task<StepResult<PlanInner>> RunStep(string step, PlanInner inner, CancellationToken ct)
    {
        switch (step)
        {
            case "draft":
                return await Ask(step, inner, Prompt(inner));
            case "approve":
                inner.Subtasks ??= Parse(inner.Reply);
                inner.ApprovalExpires = Context.Time.GetUtcNow() + ApprovalWindow;
                return new StepResult<PlanInner>(inner, new Waiting(step, WaitReason.Approval, inner.ApprovalExpires, null));
            case "start-subtasks":
                inner.Started = await StartSubtasks(inner.Subtasks ?? []);
                return new StepResult<PlanInner>(inner);
            default:
                throw new FlowFailed(step, $"Unknown step {step}.");
        }
    }

    protected override ConfirmationPlan PlanFor(string step, PlanInner inner)
        => base.PlanFor(step, inner) with
        {
            Title = $"Plan {State.Title}",
            Body = string.Join("\n", (inner.Subtasks ?? []).Select((subtask, index) => $"{index + 1}. {subtask.Title}")),
        };

    private string Prompt(PlanInner inner) =>
        $$"""
        Plan this goal as subtasks: {{(inner.Goal.Length > 0 ? inner.Goal : State.Title)}}
        Reply with only a JSON object: { "subtasks": [ { "title": "...", "type": "flow.build" or "flow.note", "inner": { ... },
        "relations": [ { "target": <index of another subtask>, "kind": "BlockedBy" or "Follows", "lag": "1.00:00:00" (optional) } ] } ] }.
        A flow.build inner is { "kind": "unit", "goal": "..." }; a flow.note inner is {}.
        """;

    // Any shape the plan does not have fails the draft as "plan: <reason>", never as an unhandled exception out of the step.
    private static PlanSubtask[] Parse(string? reply)
    {
        try { return ParseShape(ParseReply<JsonElement>("draft", reply, Json)); }
        catch (FlowFailed failed) { throw new FlowFailed("draft", "plan: " + failed.Message); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new FlowFailed("draft", "plan: " + error.Message);
        }
    }

    private static PlanSubtask[] ParseShape(JsonElement draft)
    {
        if (draft.ValueKind != JsonValueKind.Object || !draft.TryGetProperty("subtasks", out var list) || list.ValueKind != JsonValueKind.Array) { throw new FlowFailed("draft", "the plan has no subtasks"); }
        var subtasks = new List<PlanSubtask>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { throw new FlowFailed("draft", "a subtask is not an object"); }
            var title = Text(item, "title");
            var type = Text(item, "type");
            if (string.IsNullOrWhiteSpace(title)) { throw new FlowFailed("draft", "a subtask has no title"); }
            if (type is null || !Types.Contains(type)) { throw new FlowFailed("draft", $"subtask {title} has type {type}; a subtask is {string.Join(" or ", Types)}"); }
            var relations = new List<PlanRelation>();
            if (item.TryGetProperty("relations", out var r) && r.ValueKind != JsonValueKind.Null)
            {
                if (r.ValueKind != JsonValueKind.Array) { throw new FlowFailed("draft", $"subtask {title} relations are not a list"); }
                foreach (var relation in r.EnumerateArray()) { relations.Add(Relation(title, relation)); }
            }
            subtasks.Add(new PlanSubtask
            {
                Title = title,
                Type = type,
                InnerJson = item.TryGetProperty("inner", out var i) && i.ValueKind == JsonValueKind.Object ? i.GetRawText() : "{}",
                Relations = [.. relations],
            });
        }
        for (var index = 0; index < subtasks.Count; index++)
        {
            foreach (var relation in subtasks[index].Relations)
            {
                if (relation.Target < 0 || relation.Target >= subtasks.Count || relation.Target == index) { throw new FlowFailed("draft", $"subtask {index} relates to {relation.Target}"); }
            }
        }
        return [.. subtasks];
    }

    private static string? Text(JsonElement item, string name)
        => !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FlowFailed("draft", $"{name} is not a string");

    // Kinds parse case-insensitively and are stored by their enum name.
    private static PlanRelation Relation(string title, JsonElement relation)
    {
        if (relation.ValueKind != JsonValueKind.Object) { throw new FlowFailed("draft", $"a relation of {title} is not an object"); }
        if (!relation.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Number || !target.TryGetInt32(out var index))
        { throw new FlowFailed("draft", $"a relation of {title} has no numeric target"); }
        var kind = Text(relation, "kind") ?? nameof(RelationKind.BlockedBy);
        if (!Enum.TryParse<RelationKind>(kind, ignoreCase: true, out var parsed) || parsed is not (RelationKind.BlockedBy or RelationKind.Follows))
        { throw new FlowFailed("draft", $"relation kind {kind}"); }
        var lag = Text(relation, "lag");
        if (lag is not null && !TimeSpan.TryParse(lag, CultureInfo.InvariantCulture, out _)) { throw new FlowFailed("draft", $"lag {lag}"); }
        return new PlanRelation { Target = index, Kind = parsed.ToString(), Lag = lag };
    }

    private async Task<string[]> StartSubtasks(PlanSubtask[] subtasks)
    {
        var ids = new string[subtasks.Length];
        for (var index = 0; index < subtasks.Length; index++)
        {
            var subtask = subtasks[index];
            var key = $"{Id.Key}-{index}";
            var id = new NeuronId(Id.Brain, subtask.Type, key);
            ids[index] = id.ToString();
            // Started on the subtask itself, not through the index: the index calls flows, and this flow waiting on it is a cycle.
            // The index folds the subtask from its FlowStarted.
            var child = Context.Get<IFlowStarter>(id);
            if ((await child.Head()).Status == FlowStatus.Pending) { await child.StartFromJson(subtask.Title, State.Owner, Id.ToString(), subtask.InnerJson); }
            await Relate(new FlowRelation(ids[index], RelationKind.Subtask, null));
        }
        for (var index = 0; index < subtasks.Length; index++)
        {
            foreach (var relation in subtasks[index].Relations)
            {
                var lag = relation.Lag is { } text ? TimeSpan.Parse(text, CultureInfo.InvariantCulture) : (TimeSpan?)null;
                await Context.Get<IFlowStarter>(new NeuronId(Id.Brain, subtasks[index].Type, $"{Id.Key}-{index}"))
                    .Relate(new FlowRelation(ids[relation.Target], Enum.Parse<RelationKind>(relation.Kind), lag));
            }
        }
        return ids;
    }
}
