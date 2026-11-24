/*
# Creator
Role: Default
Shipped: true
Operations: open, select, trace, replay, scenario, share, run, approve, reject, ui.command

Topology, Accordant state-space, scenario & 1-event-at-a-time time-travel debugger for the DigitalBrain OS.
Left: catalog units, installed apps, active build/engineer flows, and studio launchers.
Center: live BrainScene topology (with causal replay pulses and Flame camera HUD), embedded Live Studio preview,
Accordant StateGraphView, and the ISlider + ICommandBar time-travel transport bar.
Right: 5-tab Modules.UI Inspector (State via IPropertyGrid, Source via ICodeEditor, Signals via ITable, State Graph, Scenarios).
*/
using IntoChat.OS.Host.Apps;
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.CommandBar.Signals;
using IntoChat.OS.Modules.UI.PropertyGrid;
using IntoChat.OS.Modules.UI.Slider;
using IntoChat.OS.Modules.UI.Slider.Signals;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Table.Signals;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.Tabs.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AppRef = IntoChat.OS.Host.Primitives.AppRef;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Apps.Creator;

[DigitalBrain.Signal("creator.open")]
public sealed record CreatorOpen(string? Input = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.select")]
public sealed record CreatorSelect(string? Key = null, string? Value = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.trace")]
public sealed record CreatorTrace(string? TraceId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.replay")]
public sealed record CreatorReplay(string? Cursor = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.scenario")]
public sealed record CreatorScenario(string? Alias = null, string? Name = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.share")]
public sealed record CreatorShare(string? Alias = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.run")]
public sealed record CreatorRun(string? RunId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.start-workflow")]
public sealed record CreatorStartWorkflow(string? Workflow = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.approve")]
public sealed record CreatorApprove(string? ApprovalId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.reject")]
public sealed record CreatorReject(string? ApprovalId = null) : DigitalBrain.Signal;

[Neuron("app.creator"), Screen("app.creator", Title = "Creator", Icon = "cpu", Category = "Studio", Dock = 7)]
public sealed partial class CreatorApp : AggregateScreen<CreatorWorkspaceState>, INavigatedTo<StudioNavArgs>, INavigatedFrom
{
    private static readonly ILogger ConsoleLogger = IntoChat.OS.Host.Logging.OsConsoleLog.For<CreatorApp>();

    private readonly ILogger _logger;
    private readonly TimeProvider? _time;

    public CreatorApp() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public CreatorApp(ILogger<CreatorApp>? logger = null, TimeProvider? time = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
        _time = time;
    }

    public async Task OnNavigatedTo(NavigationArrival<StudioNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var next = State.ApplyArrival(arrival.Args);
        if (next != State)
        {
            await Show(next);
        }

        await RefreshAsync();
        await InspectAsync();
        await SyncUiNeuronsAsync();
    }

    public Rule CanNavigateFrom(NavigationDeparture departure)
    {
        ArgumentNullException.ThrowIfNull(departure);
        bool inFlight;
        lock (_running)
        {
            inFlight = _running.Count > 0 || State.ScenarioInFlight;
        }

        return (State with { ScenarioInFlight = inFlight }).CanDepart(departure.Force);
    }

    public Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return Task.CompletedTask;
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);

        await TryFollowOptionalAsync<IntoChat.OS.Modules.Apps.Runs.IRuns, IntoChat.OS.Modules.Apps.Runs.RunsChanged>("runs/index");
        await TryFollowOptionalAsync<ITabs, TabChanged>(CreatorView.InspectorTabsKey);
        await TryFollowOptionalAsync<ISlider, SliderChanged>(CreatorView.ReplaySliderKey);
        await TryFollowOptionalAsync<ICommandBar, CommandBarInvoked>(CreatorView.ReplayCommandBarKey);
        await TryFollowOptionalAsync<ITable, TableRowSelected>(CreatorView.InspectorSignalsKey);

        await RefreshAsync();
        await SyncUiNeuronsAsync();
    }

    [DigitalBrain.Neurons.On]
    public Task RunsChanged(IntoChat.OS.Modules.Apps.Runs.RunsChanged? changed)
        => changed is null ? Task.CompletedTask : RefreshAsync();

    [DigitalBrain.Neurons.On]
    public async Task OnInspectorTabChanged(TabChanged? changed)
    {
        if (changed is null || string.IsNullOrWhiteSpace(changed.TabsId) || string.IsNullOrWhiteSpace(changed.SelectedId))
        {
            return;
        }

        if (!MatchesNeuronSourceKey(changed.TabsId, CreatorView.InspectorTabsKey) || changed.Version <= _lastSyncedTabsVersion)
        {
            return;
        }

        _lastSyncedTabsVersion = Math.Max(_lastSyncedTabsVersion, changed.Version);
        var normalized = CreatorWorkspaceState.TryNormalizeInspectorTab(changed.SelectedId);
        if (normalized is null || string.Equals(State.InspectorTab, normalized, StringComparison.Ordinal))
        {
            return;
        }

        await ApplyDecisionAsync(State.SelectInspectorTab(normalized));
        await SyncUiNeuronsAsync(skipTabsConfigure: true);
    }

    [DigitalBrain.Neurons.On]
    public async Task OnReplaySliderChanged(SliderChanged? changed)
    {
        if (changed is null || string.IsNullOrWhiteSpace(changed.SliderId))
        {
            return;
        }

        if (!MatchesNeuronSourceKey(changed.SliderId, CreatorView.ReplaySliderKey) || changed.Version <= _lastSyncedSliderVersion)
        {
            return;
        }

        _lastSyncedSliderVersion = Math.Max(_lastSyncedSliderVersion, changed.Version);

        var steps = ReplayScript.Steps(ActiveTimelineHops);
        if (steps.Length == 0)
        {
            return;
        }

        var index = Math.Clamp((long)Math.Round(changed.Value) - 1L, 0L, steps.Length - 1L);
        if (State.ReplayCursor == index)
        {
            return;
        }

        await ApplyDecisionAsync(State.StepTimeline(index.ToString(CultureInfo.InvariantCulture), steps.Length));
        await SyncUiNeuronsAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task OnReplayCommandBarInvoked(CommandBarInvoked? invoked)
    {
        if (invoked is null || string.IsNullOrWhiteSpace(invoked.BarId) || string.IsNullOrWhiteSpace(invoked.ItemId))
        {
            return;
        }

        if (!MatchesNeuronSourceKey(invoked.BarId, CreatorView.ReplayCommandBarKey))
        {
            return;
        }

        await Replay(new CreatorReplay(invoked.ItemId));
    }

    [DigitalBrain.Neurons.On]
    public async Task OnSignalsRowSelected(TableRowSelected? selected)
    {
        if (selected is null || string.IsNullOrWhiteSpace(selected.TableId) || selected.RowIndex is not { } rowIndex || rowIndex < 0)
        {
            return;
        }

        if (!MatchesNeuronSourceKey(selected.TableId, CreatorView.InspectorSignalsKey))
        {
            return;
        }

        var hops = ActiveTimelineHops;
        if (rowIndex < hops.Count)
        {
            var chosenHop = hops[rowIndex];
            var steps = ReplayScript.Steps(hops);
            var stepIndex = Array.FindIndex(steps, s => s.Sequence == chosenHop.Sequence);
            if (stepIndex >= 0 && State.ReplayCursor != stepIndex)
            {
                await ApplyDecisionAsync(State.StepTimeline(stepIndex.ToString(CultureInfo.InvariantCulture), steps.Length));
                await SyncUiNeuronsAsync();
                return;
            }
        }

        if (_inspection.Kind == Selection.Edge && rowIndex < _inspection.Signals.Count)
        {
            var entry = _inspection.Signals[rowIndex];
            if (!string.IsNullOrWhiteSpace(entry.TraceId))
            {
                await Trace(new CreatorTrace(entry.TraceId));
            }
        }
    }

    private ILogger Log => _logger;

    private DateTimeOffset NowUtc()
    {
        if (_time is not null) { return _time.GetUtcNow(); }
        try { return Context.Time.GetUtcNow(); }
        catch (InvalidOperationException) { return TimeProvider.System.GetUtcNow(); }
    }

    private string TryBrainIdValue()
    {
        try { return Id.Brain.Value; }
        catch (InvalidOperationException error)
        {
            Log.LogDebug(error, "Creator brain id is unavailable outside neuron activation.");
            return string.Empty;
        }
    }

    private const string FlowsKey = "flows/index";
    private const string ApprovalsKey = "approvals/index";
    private const string SynapsesKey = "ino/synapses";

    [Slot(Key = FlowsKey, Required = false)] private partial IFlows? Flows { get; }
    [Slot(Key = ApprovalsKey, Required = false)] private partial IApprovals? Approvals { get; }
    [Slot(Key = SynapsesKey, Required = false)] private partial ISynapseStudio? SynapseStudio { get; }
    [Slot(Key = "synapses/definitions", Required = false)] private partial IntoChat.OS.Modules.Apps.Synapses.ISynapseDefinitions? SynapseDefinitions { get; }
    [Slot(Key = IDebuggerReads.DefaultKey, Required = false)] private partial IDebuggerReads? DebuggerReads { get; }
    [Slot(Key = ITraceReader.DefaultKey, Required = false)] private partial ITraceReader? TraceReader { get; }
    [Slot(Key = IntoChat.OS.Modules.AI.Agents.Context.IUiIntrospector.DefaultKey, Required = false)] private partial IntoChat.OS.Modules.AI.Agents.Context.IUiIntrospector? UiIntrospector { get; }
    [Slot(Key = "profile", Required = false)] private partial IProfile? Profile { get; }
    [Slot(Key = IConfirmations.ConfirmationsNeuronKey, Required = false)] private partial IConfirmationInbox? ConfirmationInbox { get; }
    [Slot(Key = CreatorView.InspectorTabsKey, Required = false)] private partial ITabs? InspectorTabs { get; }
    [Slot(Key = CreatorView.ReplaySliderKey, Required = false)] private partial ISlider? ReplaySlider { get; }
    [Slot(Key = CreatorView.ReplayCommandBarKey, Required = false)] private partial ICommandBar? ReplayCommandBar { get; }
    [Slot(Key = CreatorView.InspectorStateKey, Required = false)] private partial IPropertyGrid? InspectorState { get; }
    [Slot(Key = CreatorView.InspectorSourceKey, Required = false)] private partial ICodeEditor? InspectorSource { get; }
    [Slot(Key = CreatorView.InspectorSignalsKey, Required = false)] private partial ITable? InspectorSignals { get; }
    [Slot(Required = false)] private partial SlotCollection<IntoChat.OS.Modules.AI.Workflows.IWorkflowRun> WorkflowRuns { get; }
    [Slot(Required = false)] private partial SlotCollection<IApp> Apps { get; }
    public const int EdgeSignals = 20;
    public const int MaxHops = 2000;

    private BrainTopology _topology = new([], []);
    private FlowView[] _flows = [];
    private Approval[] _approvals = [];
    private IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition[] _synapses = [];
    private DigitalBrain.Units.DeployedSource[] _deployedUnits = [];
    private Inspection _inspection = Inspection.None;
    private TraceHop[] _retainedTimelineHops = [];
    private int _lastSyncedSliderVersion;
    private int _lastSyncedTabsVersion;
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    private IReadOnlyList<TraceHop> ActiveTimelineHops
        => _inspection.Hops.Count > 0 ? _inspection.Hops : _retainedTimelineHops;

    public TimeSpan ScenarioBudget { get; set; } = TimeSpan.FromSeconds(30);

    public Func<string, IReadOnlyDictionary<string, string>, string?, CancellationToken, Task<string>> RunScenarios { get; set; } = ScenarioTranscripts.Run;

    public const string AppName = ShippedApps.Creator;

    public static AppBundle Bundle => AppBundle.FromScreen<CreatorApp>(
        "creator",
        "3.0.0",
        description: "Inspect the DigitalBrain topology, time-travel causal signals one event at a time, explore Accordant state spaces, and run unit scenarios.",
        operations:
        [
            new("open", "Show the brain; input trace/{traceId} opens that trace, node:{id} selects that node."),
            new("select", "Inspect a node, an edge, a pulse, or switch Studio view/tab."),
            new("trace", "Inspect a whole trace by its id."),
            new("replay", "Step the replay timeline: first, prev, toggle, next, last, a step index, or stop."),
            new("scenario", "Run a unit's kept scenarios on fakes and show the transcript."),
            new("share", "Publish the inspected unit as an interactive App-Post on your profile."),
            new("start-workflow", "Start a run of a named workflow."),
            new("run", "Inspect an active change run pipeline and unified diff."),
            new("approve", "Approve and promote a pending change run."),
            new("reject", "Reject and abort a pending change run."),
            new(UiCommand.SignalAlias, "Dispatch a typed UI Kit aggregate command."),
        ],
        sideWidth: 340d);

    [DigitalBrain.Neurons.On]
    public async Task Open(CreatorOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var next = State.OpenInput(request.Input);
        if (next != State)
        {
            await Show(next);
        }

        await RefreshAsync();
        await InspectAsync();
        await SyncUiNeuronsAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Select(CreatorSelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = Blank(request.Value) ?? Blank(request.Key);
        if (string.Equals(raw, CreatorView.ToggleStateGraphTarget, StringComparison.Ordinal))
        {
            await Show(State.ToggleStateGraph());
            await SyncUiNeuronsAsync();
            return;
        }

        if (raw is not null && raw.StartsWith("mode:", StringComparison.Ordinal))
        {
            await ApplyDecisionAsync(State.SwitchCanvasMode(raw["mode:".Length..]));
            await SyncUiNeuronsAsync();
            return;
        }

        if (raw is not null && raw.StartsWith("tab:", StringComparison.Ordinal))
        {
            await ApplyDecisionAsync(State.SelectInspectorTab(raw["tab:".Length..]));
            await SyncUiNeuronsAsync();
            return;
        }

        if (raw is not null && raw.StartsWith("file:", StringComparison.Ordinal))
        {
            await Show(State.SetSourceFile(raw["file:".Length..]));
            await SyncUiNeuronsAsync();
            return;
        }

        if (raw is not null && raw.StartsWith("viewport:", StringComparison.Ordinal))
        {
            await Show(State.SetPreviewViewport(raw["viewport:".Length..]));
            return;
        }

        var selection = Selection.Parse(raw);
        if (_topology.Nodes.Count == 0)
        {
            await RefreshAsync();
        }

        if (selection is not null && !Known(selection))
        {
            _inspection = new Inspection(selection.Kind, selection.Id) { Notice = "Not in the brain graph." };
            await Show(State with { Selected = selection, Transcript = null });
            await SyncUiNeuronsAsync();
            return;
        }

        if (selection is null)
        {
            _inspection = Inspection.None;
            _retainedTimelineHops = [];
            await Show(State.ClearSelection());
            await SyncUiNeuronsAsync();
            return;
        }

        await Show(State.SelectSelection(selection, ActiveTimelineHops.Count));
        await InspectAsync();
        await SyncUiNeuronsAsync();
    }

    [DigitalBrain.Neurons.On]
    public Task Trace(CreatorTrace request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Blank(request.TraceId) is { } traceId
            ? Select(new CreatorSelect(BrainGraph.PulseKey(traceId)))
            : Task.CompletedTask;
    }

    [DigitalBrain.Neurons.On]
    public async Task Replay(CreatorReplay request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stepCount = ReplayScript.Steps(ActiveTimelineHops).Length;
        await Show(State.StepReplay(request.Cursor, stepCount));
        await SyncUiNeuronsAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Scenario(CreatorScenario request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((Blank(request.Alias) ?? SelectedAlias()) is not { } alias)
        {
            await ApplyDecisionAsync(State.ValidateUnitSelection());
            return;
        }

        bool alreadyRunning;
        lock (_running)
        {
            alreadyRunning = !_running.Add(alias);
        }

        if (alreadyRunning)
        {
            await Show(State with { Status = $"The scenarios of {alias} are already running." });
            return;
        }

        string transcript;
        using var budget = new CancellationTokenSource(ScenarioBudget);
        try
        {
            transcript = DebuggerReads is { } reads && await reads.Loaded(alias)
                ? await RunScenarios(alias, await FilesAsync(alias), Blank(request.Name), budget.Token)
                : $"{alias} is not a loaded unit.";
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            transcript = $"✗ budget: the scenarios of {alias} did not finish within {ScenarioBudget.TotalSeconds:0.#}s";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "A scenario run that fails says why in its transcript.");
            transcript = $"The scenarios did not run: {error.Message}";
        }
        finally
        {
            lock (_running) { _running.Remove(alias); }
        }

        _inspection = _inspection with
        {
            ScenarioPassRate = CreatorStateSpaceInspector.ComputeTranscriptPassRate(transcript) ?? _inspection.ScenarioPassRate,
        };
        await Show(State with
        {
            Transcript = transcript,
            InspectorTab = "scenarios",
            Status = $"Ran the scenarios of {alias}",
        });
        await SyncUiNeuronsAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Share(CreatorShare request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((Blank(request.Alias) ?? SelectedAlias()) is not { } alias)
        {
            await Show(State with { Status = "Select a unit to share." });
            return;
        }

        try
        {
            var files = await FilesAsync(alias);
            var csSource = string.Join("\n\n", files
                .Where(kv => kv.Key.EndsWith(".cs", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value));
            var summary = files.TryGetValue("spec.md", out var spec) && !string.IsNullOrWhiteSpace(spec)
                ? spec.Trim()
                : $"Shared unit {alias}";
            var brainId = State.Selected is { Kind: Selection.Node, Id: { Length: > 0 } nodeId }
                && nodeId.IndexOf('/', StringComparison.Ordinal) is > 0 and var slash
                ? nodeId[..slash]
                : TryBrainIdValue();
            var embeddedTarget = BrainId.IsValid(brainId) && IntoChat.OS.Host.Primitives.UnitAlias.TryParse(alias, out var parsedAlias)
                ? parsedAlias.StateNeuron(new BrainId(brainId)).ToString()
                : alias;
            var post = new ProfilePostItem(
                Id: $"post-{alias}",
                Content: summary,
                CreatedAt: NowUtc(),
                Circle: "",
                Title: alias,
                TemplateAppId: alias,
                TemplateSource: csSource,
                EmbeddedApps: [new("main", alias, alias, embeddedTarget)],
                UnitFiles: files.Count > 0 ? files : null,
                AuthorBrainId: brainId)
            {
                Audience = new Audience.Public(),
            };

            if (Profile is not { } profile)
            {
                await Show(State with { Status = $"Could not share {alias}: profile neuron unavailable." });
                return;
            }

            await profile.PublishAppPost(post);
            await Show(State with { Status = $"Shared {alias} as App-Post post-{alias}" });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Sharing to profile is optional when the profile neuron is not registered.");
            await Show(State with { Status = $"Could not share {alias}: {error.Message}" });
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task Run(CreatorRun request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.RunId) is { } runId)
        {
            await Show(State.SelectRun(runId));
        }

        await RefreshAsync();
        await InspectAsync();
        await SyncUiNeuronsAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task StartWorkflow(CreatorStartWorkflow request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.Workflow) is not { } workflow)
        {
            await Show(State with { Status = "Choose a workflow to run." });
            return;
        }

        var runKey = $"workflows/{Guid.NewGuid():N}";
        try
        {
            var run = await WorkflowRuns[runKey].Start(workflow, workflow);
            await Show(State with
            {
                Status = run.Status == IntoChat.OS.Modules.AI.Workflows.WorkflowRunStatus.Waiting
                    ? $"{workflow} is waiting for your approval ({runKey})."
                    : $"{workflow}: {run.Status} ({runKey}).",
            });
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            await Show(State with { Status = $"Could not run {workflow}: {error.Message}" });
        }

        await RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Approve(CreatorApprove request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var approvalId = Blank(request.ApprovalId) ?? _approvals.FirstOrDefault()?.PlanId;
        if (approvalId is null) { return; }

        try
        {
            if (ConfirmationInbox is not { } inbox)
            {
                await Show(State with { Status = $"Could not approve {approvalId}: confirmations inbox unavailable." });
                return;
            }

            await inbox.Resolve(approvalId, true);
            await Show(State with { Status = $"Approved {approvalId}" });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Approval resolution failed.");
            await Show(State with { Status = $"Could not approve {approvalId}: {error.Message}" });
        }

        await RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Reject(CreatorReject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var approvalId = Blank(request.ApprovalId) ?? _approvals.FirstOrDefault()?.PlanId;
        if (approvalId is null) { return; }

        try
        {
            if (ConfirmationInbox is not { } inbox)
            {
                await Show(State with { Status = $"Could not reject {approvalId}: confirmations inbox unavailable." });
                return;
            }

            await inbox.Resolve(approvalId, false);
            await Show(State with { Status = $"Rejected {approvalId}" });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Reject resolution failed.");
            await Show(State with { Status = $"Could not reject {approvalId}: {error.Message}" });
        }

        await RefreshAsync();
    }

    public new CreatorWorkspaceState State => base.State;
    public View Render() => PreviewView(State);
    protected override Node Render(CreatorWorkspaceState state)
        => CreatorView.RenderNode(state, _topology, _flows, _approvals, _inspection, ActiveTimelineHops, _synapses, ActiveTransition, _deployedUnits);

    private async Task RefreshAsync()
    {
        if (Flows is { } flows)
        {
            try { _flows = await flows.List(new FlowQuery()); }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "The flows index is optional."); }
        }
        if (Approvals is { } approvals)
        {
            try { _approvals = [.. await approvals.Pending()]; }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Approvals are optional."); }
        }
        if (SynapseDefinitions is { } synapseDefs)
        {
            try { _synapses = await synapseDefs.List(); }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Synapse catalog is optional."); }
        }
        if (SynapseStudio is { } studio)
        {
            try { _deployedUnits = await studio.List(); }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Synapse studio list is optional."); }
        }
        if (DebuggerReads is { } reads)
        {
            try { _topology = await reads.Topology(); }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Topology read is optional."); }
        }
        _topology = _topology with { Flows = _flows };
        await Show(State with { Status = State.Status });
    }

    private async Task InspectAsync()
    {
        _inspection = State.Selected switch
        {
            null when State.ActiveRunId is { Length: > 0 } runId => await FlowRunAsync(runId),
            null => Inspection.None,
            { Kind: Selection.Pulse } pulse => await PulseAsync(pulse.Id),
            { Kind: Selection.Edge } edge => await EdgeAsync(edge.Id),
            { } node => await NodeAsync(node.Id),
        };

        if (_inspection.Hops.Count > 0)
        {
            _retainedTimelineHops = [.. _inspection.Hops];
        }

        var stepCount = ReplayScript.Steps(ActiveTimelineHops).Length;
        await Show(State with { ReplayStepCount = stepCount });
    }

    private Task TryFollowOptionalAsync<TNeuron, TSignal>(string key)
        where TNeuron : class
        where TSignal : DigitalBrain.Signal
        => FollowOptionalAsync<TNeuron, TSignal>(key, Log);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
