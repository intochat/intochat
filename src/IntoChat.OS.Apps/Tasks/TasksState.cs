using DigitalBrain.Flows;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Tasks;

// View is board, list or timeline; Query filters the flows shown; Selected is the flow whose card is in the side panel.
public sealed record TasksState(string View, FlowQuery Query, string? Selected)
{
    public TasksState() : this("board", new FlowQuery(), null) { }

    public string Status { get; init; } = "Ready";

    // The relation kind the Relate picker applies to the next target chosen.
    public string RelateKind { get; init; } = "BlockedBy";

    // The selected build flow's deployed unit, shown by Show source.
    public string? Source { get; init; }

    // What the new-task form and the lag field hold, folded from their text fields' changes.
    public TasksDraft Draft { get; init; } = new();

    [Invariant]
    public bool ValidViewMode => View is "board" or "list" or "timeline";

    public Rule EmptyTitle => Decision.Rule("empty_title", Draft, static d => string.IsNullOrWhiteSpace(d.Title), "Enter a task title.");

    public TasksState SwitchView([Sample("board", "list", "timeline")] string? view)
    {
        var normalized = view?.Trim().ToLowerInvariant();
        return this with { View = normalized is "list" or "timeline" ? normalized : "board" };
    }

    public TasksState SelectFlow([Sample("local/flow.plan/1")] string? flowId)
        => this with { Selected = string.IsNullOrWhiteSpace(flowId) ? null : flowId.Trim(), Source = null };

    [IgnoreExplore]
    public TasksState ApplyFilter(
        string? owner,
        FlowStatus? status,
        string? type)
        => this with
        {
            Query = Query with
            {
                Owner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim(),
                Status = status,
                Type = string.IsNullOrWhiteSpace(type) ? null : type.Trim(),
            },
        };

    public TasksState SetRelateKind([Sample("BlockedBy", "Follows")] string? kind)
        => Enum.TryParse<RelationKind>(kind, ignoreCase: true, out var parsed)
            ? this with { RelateKind = parsed.ToString() }
            : this;

    public TasksState UpdateDraftField([Sample("title", "description", "lag")] string field, [Sample("Ship docs")] string? rawValue)
    {
        var value = rawValue ?? string.Empty;
        return this with
        {
            Draft = field switch
            {
                "title" => Draft with { Title = value },
                "description" => Draft with { Description = value },
                _ => Draft with { Lag = value },
            },
        };
    }

    [IgnoreExplore]
    public TasksState MarkStarted(string flowId, string title)
        => this with
        {
            Selected = flowId,
            Source = null,
            Status = $"Started {title}",
            Draft = Draft with { Title = "", Description = "" },
        };

    [IgnoreExplore]
    public TasksState WithSource(string flowId, string? source)
        => this with { Selected = flowId, Source = source };

    [IgnoreExplore]
    public TasksState WithStatus(string status)
        => this with { Status = status ?? string.Empty };
}

public sealed record TasksDraft(string Title = "", string Description = "", string Lag = "");

// One flow's bar on the timeline.
public sealed record TimelineBar(string FlowId, string Title, DateTimeOffset Start, DateTimeOffset End);

// One board column and the flows in it.
public sealed record BoardColumn(string Name, FlowView[] Flows);
