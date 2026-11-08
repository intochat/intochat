using IntoChat.OS.Modules.Apps.Flows;

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
}

// One flow's bar on the timeline.
public sealed record TimelineBar(string FlowId, string Title, DateTimeOffset Start, DateTimeOffset End);

// One board column and the flows in it.
public sealed record BoardColumn(string Name, FlowView[] Flows);
