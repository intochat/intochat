/*
# Tasks
Role: Default
Shipped: true
Operations: open, view, filter, select, new, cancel, relate-kind, relate, show-source, ask-ino

The planning surface over the brain's flows: a board by status, a sortable list and a timeline, with a card per flow that answers
its pending approval through the confirmations neuron, cancels or relates it, opens its trace and, for a build, shows its unit.
*/
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter.Fields;
using DigitalBrain.Log;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Tasks;

[Signal("tasks.open")]
public sealed record TasksOpen : Signal;

[Signal("tasks.view")]
public sealed record TasksView(string? View = null) : Signal;

[Signal("tasks.filter")]
public sealed record TasksFilter(string? Owner = null, string? Status = null, string? Type = null) : Signal;

[Signal("tasks.select")]
public sealed record TasksSelect(string? FlowId = null) : Signal;

[Signal("tasks.new")]
public sealed record TasksNew(string? Title = null, string? Description = null) : Signal;

[Signal("tasks.cancel")]
public sealed record TasksCancel(string? FlowId = null, string? Reason = null) : Signal;

[Signal("tasks.relate-kind")]
public sealed record TasksRelateKind(string? Kind = null) : Signal;

[Signal("tasks.relate")]
public sealed record TasksRelate(string? FlowId = null, string? Kind = null, string? Target = null, string? Lag = null) : Signal;

[Signal("tasks.show-source")]
public sealed record TasksShowSource(string? FlowId = null) : Signal;

[Signal("tasks.ask-ino")]
public sealed record TasksAskIno(string? FlowId = null) : Signal;

[Neuron("app.tasks")]
public sealed class TasksApp : Screen<TasksState>
{
    private const string FlowsKey = "flows/index";
    private const string ApprovalsKey = "approvals/index";
    private const string SynapsesKey = "ino/synapses";

    private static readonly ILogger ConsoleLog = IntoChat.OS.Framework.Logging.OsConsoleLog.For<TasksApp>();
    private readonly ILogger<TasksApp>? _logger;

    public TasksApp() : this(null) { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public TasksApp(ILogger<TasksApp>? logger = null) => _logger = logger;

    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        return base.OnActivate(cancellationToken);
    }

    private ILogger Log => _logger ?? ConsoleLog;

    // Render cannot await: the last flows and approvals read, refreshed on open, after each command and on each change signal.
    private FlowView[] _flows = [];
    private Approval[] _approvals = [];

    // The clock the timeline projects running flows against.
    public TimeProvider Time { get; set; } = TimeProvider.System;

    public const string AppName = "tasks";

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

    [DigitalBrain.Neurons.On]
    public Task Open(TasksOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task View(TasksView request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Show(State.SwitchView(request.View));
    }

    [DigitalBrain.Neurons.On]
    public async Task Filter(TasksFilter request)
    {
        ArgumentNullException.ThrowIfNull(request);
        FlowStatus? status = Enum.TryParse<FlowStatus>(request.Status, ignoreCase: true, out var parsed) ? parsed : null;
        await Show(State.ApplyFilter(request.Owner, status, request.Type));
        await RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Select(TasksSelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Show(State.SelectFlow(request.FlowId));
    }

    [DigitalBrain.Neurons.On]
    public async Task New(TasksNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = Blank(request.Title) ?? "New task";
        // PlanFlow reads its inner state with web JSON: the description is the plan's goal.
        var inner = JsonSerializer.Serialize(new PlanInner { Goal = request.Description?.Trim() ?? title }, JsonSerializerOptions.Web);
        try
        {
            var flowId = await Get<IFlows>(FlowsKey).Start(PlanFlow.Alias, title, null, inner);
            await Show(State.MarkStarted(flowId, title));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "The flow could not start; the status line reports why.");
            await Show(State.WithStatus($"Could not start: {error.Message}"));
        }
        await RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Cancel(TasksCancel request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        try
        {
            await Get<IFlows>(FlowsKey).Cancel(flowId, Blank(request.Reason) ?? "Cancelled from Tasks");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "The cancel was refused; the status line reports why.");
            await Show(State with { Status = $"Could not cancel: {error.Message}" });
        }
        await RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task RelateKind(TasksRelateKind request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = State.SetRelateKind(request.Kind);
        if (next != State) { await Show(next); }
    }

    [DigitalBrain.Neurons.On]
    public async Task Relate(TasksRelate request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId || Blank(request.Target) is not { } target) { return; }
        var kind = Enum.TryParse<RelationKind>(request.Kind ?? State.RelateKind, ignoreCase: true, out var parsed) ? parsed : RelationKind.BlockedBy;
        TimeSpan? lag = TasksViewRenderer.ParseLag(request.Lag) is { } days ? days : TimeSpan.TryParse(request.Lag, System.Globalization.CultureInfo.InvariantCulture, out var span) ? span : null;
        try
        {
            await Get<IFlows>(FlowsKey).Relate(flowId, new FlowRelation(target, kind, lag));
            await Show(State with { Status = $"Related {kind}" });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "The relation was refused; the status line reports why.");
            await Show(State with { Status = $"Could not relate: {error.Message}" });
        }
        await RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task ShowSource(TasksShowSource request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        var unit = await UnitOfAsync(flowId);
        await Show(State.WithSource(flowId, unit is null ? await StagedSourceAsync(flowId) : await SourceOfAsync(unit)));
    }

    [DigitalBrain.Neurons.On]
    public async Task AskIno(TasksAskIno request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.FlowId) is not { } flowId) { return; }
        var unit = await UnitOfAsync(flowId);
        var alias = unit?.Aliases.FirstOrDefault() ?? unit?.SourceKey ?? AliasOrKey(flowId);
        // The focus carries the open-ino action; the shell's presence layer opens Ino from it.
        await Get<IInoFocus>(IInoFocus.DefaultKey).Point("window:ino", FocusTone.Info, $"Change unit {alias}", new FocusAction("open", "ino"));
        await Show(State.WithStatus($"Asked Ino to change {alias}"));
    }

    [DigitalBrain.Neurons.On]
    public Task OnFlowsChanged(FlowsChanged changed)
    {
        _ = changed;
        return RefreshAsync();
    }

    // The signal names the change; the value is read from the field.
    public async Task OnField(string field, FieldChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var value = (await Get<IField>(TasksViewRenderer.FieldKey(field)).Read()).Value ?? "";
        await Show(State.UpdateDraftField(field, value));
    }

    public new TasksState State => base.State;
    public View Render() => Render(State);
    public View RenderSide() => TasksViewRenderer.RenderSide(State, _flows, _approvals, State.Source);
    protected override View Render(TasksState state) => TasksViewRenderer.Render(state, _flows, _approvals, Time.GetUtcNow());

    private async Task RefreshAsync()
    {
        try { _flows = await Get<IFlows>(FlowsKey).List(State.Query); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "The flows index is optional; the board shows the last read."); }
        try { _approvals = [.. await Get<IApprovals>(ApprovalsKey).Pending()]; }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "Approvals are optional when the inbox is not reachable."); }
        await Show(State with { Status = State.Status });
    }

    // The deployed unit whose source key or alias is the unit the flow authored (named by the install consent it waits on) or the flow's key.
    private async Task<DigitalBrain.Units.DeployedSource?> UnitOfAsync(string flowId)
    {
        var keys = new[] { AliasOf(flowId), FlowKey(flowId) }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            var units = await Get<ISynapseStudio>(SynapsesKey).List();
            return units.FirstOrDefault(u => keys.Any(key => u.SourceKey == key || u.Aliases.Contains(key, StringComparer.Ordinal)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "The synapse studio is optional; the alias falls back to the flow key.");
            return null;
        }
    }

    // The alias a build flow authored: the subject the flow named, else the SynapseStaged consent its confirmation plan raised.
    private string? AliasOf(string flowId)
    {
        var flow = _flows.FirstOrDefault(f => f.FlowId == flowId);
        if (flow?.Subject is { Length: > 0 } subject) { return subject; }
        return flow is { ConfirmationPlanId: { } plan }
            && _approvals.FirstOrDefault(a => a.Kind == ApprovalKind.SynapseStaged && a.PlanId == plan) is { } consent ? consent.Subject : null;
    }

    private string AliasOrKey(string flowId) => AliasOf(flowId) ?? FlowKey(flowId);

    // The source of a deployed unit: spec.md first, then the .cs files, then scenarios.json, each under its path.
    private async Task<string> SourceOfAsync(DigitalBrain.Units.DeployedSource unit)
    {
        var alias = unit.Aliases.FirstOrDefault() ?? unit.SourceKey;
        var header = $"{unit.SourceKey} · {unit.Status} · build {unit.Hash}\nAliases: {string.Join(", ", unit.Aliases)}\nDeployed {unit.At:u}";
        return header + await FilesOfAsync(alias);
    }

    // A flow whose unit is not live yet (waiting for consent) still shows the files the studio kept for its alias.
    private async Task<string> StagedSourceAsync(string flowId)
        => AliasOf(flowId) is { } alias && await FilesOfAsync(alias) is { Length: > 0 } files
            ? $"{alias} · staged, waiting for your consent{files}"
            : "No deployed unit matches this flow.";

    private async Task<string> FilesOfAsync(string alias)
    {
        var studio = Get<ISynapseStudio>(SynapsesKey);
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        var unavailable = false;
        try
        {
            foreach (var (path, text) in await studio.SourceFiles(alias)) { files[path] = text; }
            foreach (var (path, text) in await studio.Sidecars(alias)) { files[path] = text; }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "The unit's files are optional; the source shows what was read.");
            unavailable = true;
        }
        var ordered = files.Where(file => file.Key == "spec.md")
            .Concat(files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal))
            .Concat(files.Where(file => file.Key is not "spec.md" and not "scenarios.json" && !file.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal))
            .Concat(files.Where(file => file.Key == "scenarios.json"));
        var listing = string.Concat(ordered.Select(file => $"\n\n--- {file.Key} ---\n{file.Value}"));
        return unavailable ? listing + "\n\n(files unavailable)" : listing;
    }

    private static string FlowKey(string flowId) => flowId[(flowId.LastIndexOf('/') + 1)..];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
