/*
# Tasks
Role: Default
Shipped: true
Operations: open, view, filter, select, new, cancel, relate-kind, relate, show-source, ask-ino

The planning surface over the brain's flows: a board by status, a sortable list and a timeline, with a card per flow that answers
its pending approval through the confirmations neuron, cancels or relates it, opens its trace and, for a build, shows its unit.
*/
using System.Text.Json;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Fields;

namespace IntoChat.OS.Apps.Tasks;

[DigitalBrain.Signal("tasks.open")]
public sealed record TasksOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.view")]
public sealed record TasksView(string? View = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.filter")]
public sealed record TasksFilter(string? Owner = null, string? Status = null, string? Type = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.select")]
public sealed record TasksSelect(string? FlowId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.new")]
public sealed record TasksNew(string? Title = null, string? Description = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.cancel")]
public sealed record TasksCancel(string? FlowId = null, string? Reason = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.relate-kind")]
public sealed record TasksRelateKind(string? Kind = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.relate")]
public sealed record TasksRelate(string? FlowId = null, string? Kind = null, string? Target = null, string? Lag = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.show-source")]
public sealed record TasksShowSource(string? FlowId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("tasks.ask-ino")]
public sealed record TasksAskIno(string? FlowId = null) : DigitalBrain.Signal;

[DigitalBrain.App("tasks", Accent = "#F59E0B", Category = "Productivity")]
public sealed class TasksApp : DigitalBrain.App<TasksState>
{
    private const string FlowsKey = "flows/index";
    private const string ApprovalsKey = "approvals/index";
    private const string SynapsesKey = "ino/synapses";

    // Render cannot await: the last flows and approvals read, refreshed on open, after each command and on each change signal.
    private FlowView[] _flows = [];
    private Approval[] _approvals = [];

    // The clock the timeline projects running flows against.
    public TimeProvider Time { get; set; } = TimeProvider.System;

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(TasksApp));

    public static AppBundle Bundle { get; } = new(
        "tasks",
        "1.0.0",
        new AppManifest(
            Title: "Tasks",
            Description: "Plan and follow the brain's flows: board, list and timeline, with approvals inline.",
            Operations:
            [
                new("open", "Show the board."),
                new("view", "Switch to board, list or timeline."),
                new("filter", "Filter by owner, status and type."),
                new("select", "Show a flow's card."),
                new("new", "Start a plan flow from a title and a description."),
                new("cancel", "Cancel a flow."),
                new("relate-kind", "Pick the relation kind Relate applies."),
                new("relate", "Relate a flow to another."),
                new("show-source", "Show a build flow's deployed unit."),
                new("ask-ino", "Point Ino at a build flow's unit to change it."),
            ],
            Settings: [],
            Slots: [],
            Icon: "tasks",
            Toolbar:
            [
                new("view", Icon: "kanban", Label: "Board"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(TasksApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public Task Open(TasksOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshAsync();
    }

    [DigitalBrain.On]
    public void View(TasksView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = request.View?.Trim().ToLowerInvariant();
        Set(State with { View = view is "list" or "timeline" ? view : "board" });
    }

    [DigitalBrain.On]
    public Task Filter(TasksFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        FlowStatus? status = Enum.TryParse<FlowStatus>(request.Status, ignoreCase: true, out var parsed) ? parsed : null;
        Set(State with { Query = State.Query with { Owner = Blank(request.Owner), Status = status, Type = Blank(request.Type) } });
        return RefreshAsync();
    }

    [DigitalBrain.On]
    public void Select(TasksSelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(State with { Selected = Blank(request.FlowId), Source = null });
    }

    [DigitalBrain.On]
    public async Task New(TasksNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = Blank(request.Title) ?? "New task";
        // PlanFlow reads its inner state with web JSON: the description is the plan's goal.
        var inner = JsonSerializer.Serialize(new PlanInner { Goal = request.Description?.Trim() ?? title }, JsonSerializerOptions.Web);
        try
        {
            var flowId = await Get<IFlows>(FlowsKey).Start(PlanFlow.Alias, title, null, inner);
            Set(State with { Selected = flowId, Source = null, Status = $"Started {title}", Draft = State.Draft with { Title = "", Description = "" } });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The flow could not start; the status line reports why.");
            Set(State with { Status = $"Could not start: {error.Message}" });
        }
        await RefreshAsync();
    }

    [DigitalBrain.On]
    public async Task Cancel(TasksCancel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        await Get<IFlows>(FlowsKey).Cancel(flowId, Blank(request.Reason) ?? "Cancelled from Tasks");
        await RefreshAsync();
    }

    [DigitalBrain.On]
    public void RelateKind(TasksRelateKind request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Enum.TryParse<RelationKind>(request.Kind, ignoreCase: true, out var kind)) { Set(State with { RelateKind = kind.ToString() }); }
    }

    [DigitalBrain.On]
    public async Task Relate(TasksRelate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId || Blank(request.Target) is not { } target) { return; }
        var kind = Enum.TryParse<RelationKind>(request.Kind ?? State.RelateKind, ignoreCase: true, out var parsed) ? parsed : RelationKind.BlockedBy;
        TimeSpan? lag = TasksViewRenderer.ParseLag(request.Lag) is { } days ? days : TimeSpan.TryParse(request.Lag, System.Globalization.CultureInfo.InvariantCulture, out var span) ? span : null;
        try
        {
            await Get<IFlows>(FlowsKey).Relate(flowId, new FlowRelation(target, kind, lag));
            Set(State with { Status = $"Related {kind}" });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The relation was refused; the status line reports why.");
            Set(State with { Status = $"Could not relate: {error.Message}" });
        }
        await RefreshAsync();
    }

    [DigitalBrain.On]
    public async Task ShowSource(TasksShowSource request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        var unit = await UnitOfAsync(flowId);
        Set(State with
        {
            Selected = flowId,
            Source = unit is null
                ? "No deployed unit matches this flow."
                : $"{unit.SourceKey} · {unit.Status} · build {unit.Hash}\nAliases: {string.Join(", ", unit.Aliases)}\nDeployed {unit.At:u}",
        });
    }

    [DigitalBrain.On]
    public async Task AskIno(TasksAskIno request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        var unit = await UnitOfAsync(flowId);
        var alias = unit?.Aliases.FirstOrDefault() ?? unit?.SourceKey ?? FlowKey(flowId);
        // The focus carries the open-ino action; the shell's presence layer opens Ino from it.
        await Get<IInoFocus>(IInoFocus.DefaultKey).Point("window:ino", FocusTone.Info, $"Change unit {alias}", new FocusAction("open", "ino"));
        Set(State with { Status = $"Asked Ino to change {alias}" });
    }

    protected override void Watches(DigitalBrain.WatchBuilder watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        foreach (var field in TasksViewRenderer.Fields)
        {
            watch.On<IField, FieldChanged>($"{field}-changed", app => $"apps/{app.App}/{field}", changed => OnField(field, changed));
        }
        watch.On<IFlows, FlowsChanged>("flows-changed", _ => FlowsKey, _ => RefreshAsync());
        watch.On<IConfirmationInbox, ConfirmationRequested>("confirmation-requested", _ => IConfirmations.ConfirmationsNeuronKey, _ => RefreshAsync());
        watch.On<IConfirmationInbox, ConfirmationResolved>("confirmation-resolved", _ => IConfirmations.ConfirmationsNeuronKey, _ => RefreshAsync());
    }

    // The signal names the change; the value is read from the field.
    public async Task OnField(string field, FieldChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var value = (await Get<IField>(TasksViewRenderer.FieldKey(field)).Read()).Value ?? "";
        Set(State with
        {
            Draft = field switch
            {
                "title" => State.Draft with { Title = value },
                "description" => State.Draft with { Description = value },
                _ => State.Draft with { Lag = value },
            },
        });
    }

    public View Render() => TasksViewRenderer.Render(State, _flows, _approvals, Time.GetUtcNow());

    public View RenderSide() => TasksViewRenderer.RenderSide(State, _flows, _approvals, State.Source);

    private async Task RefreshAsync()
    {
        try { _flows = await Get<IFlows>(FlowsKey).List(State.Query); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The flows index is optional; the board shows the last read."); }
        try { _approvals = [.. await Get<IApprovals>(ApprovalsKey).Pending()]; }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "Approvals are optional when the inbox is not reachable."); }
        Set(State with { Status = State.Status });
    }

    // The deployed unit whose source key or alias is the flow's key.
    private async Task<DigitalBrain.DeployedSource?> UnitOfAsync(string flowId)
    {
        var key = FlowKey(flowId);
        try
        {
            var units = await Get<ISynapseStudio>(SynapsesKey).List();
            return units.FirstOrDefault(u => u.SourceKey == key || u.Aliases.Contains(key, StringComparer.Ordinal));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The synapse studio is optional; the alias falls back to the flow key.");
            return null;
        }
    }

    private static string FlowKey(string flowId) => flowId[(flowId.LastIndexOf('/') + 1)..];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
