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
using System.Globalization;
using DigitalBrain;
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
using AppRef = IntoChat.OS.Primitives.AppRef;

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

[Neuron("app.creator"), Screen("app.creator", Title = "Creator")]
public sealed class CreatorApp : AggregateScreen<CreatorWorkspaceState>, INavigatedTo<StudioNavArgs>, INavigatedFrom
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

        if (!MatchesNeuronKey(changed.TabsId, CreatorView.InspectorTabsKey) || changed.Version <= _lastSyncedTabsVersion)
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

        if (!MatchesNeuronKey(changed.SliderId, CreatorView.ReplaySliderKey) || changed.Version <= _lastSyncedSliderVersion)
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

        if (!MatchesNeuronKey(invoked.BarId, CreatorView.ReplayCommandBarKey))
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

        if (!MatchesNeuronKey(selected.TableId, CreatorView.InspectorSignalsKey))
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
    public const int EdgeSignals = 20;
    public const int MaxHops = 2000;

    private BrainTopology _topology = new([], []);
    private FlowView[] _flows = [];
    private Approval[] _approvals = [];
    private IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition[] _synapses = [];
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

    public static AppBundle Bundle { get; } = new(
        "creator",
        "3.0.0",
        new AppManifest(
            Title: "Creator",
            Description: "Inspect the DigitalBrain topology, time-travel causal signals one event at a time, explore Accordant state spaces, and run unit scenarios.",
            Operations:
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
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Dock: 7,
            SideWidth: 340d),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(CreatorApp).AssemblyQualifiedName,
    };

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
            transcript = await Get<IDebuggerReads>(IDebuggerReads.DefaultKey).Loaded(alias)
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
            var embeddedTarget = BrainId.IsValid(brainId) && IntoChat.OS.Primitives.UnitAlias.TryParse(alias, out var parsedAlias)
                ? parsedAlias.StateNeuron(new BrainId(brainId)).ToString()
                : alias;
            var post = new ProfilePostItem(
                Id: $"post-{alias}",
                Content: summary,
                CreatedAt: NowUtc(),
                Circle: "public",
                Title: alias,
                TemplateAppId: alias,
                TemplateSource: csSource,
                EmbeddedApps: [new("main", alias, alias, embeddedTarget)],
                UnitFiles: files.Count > 0 ? files : null,
                AuthorBrainId: brainId);

            await Get<IProfile>("profile").PublishAppPost(post);
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
            var run = await Get<IntoChat.OS.Modules.AI.Workflows.IWorkflowRun>(runKey).Start(workflow, workflow);
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
            await Get<IConfirmationInbox>(IConfirmationInbox.Key(Id.Brain.Value)).Resolve(approvalId, true);
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
            await Get<IConfirmationInbox>(IConfirmationInbox.Key(Id.Brain.Value)).Resolve(approvalId, false);
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
        => CreatorView.RenderNode(state, _topology, _flows, _approvals, _inspection, ActiveTimelineHops, _synapses, ActiveTransition);

    private async Task RefreshAsync()
    {
        try { _flows = await Get<IFlows>(FlowsKey).List(new FlowQuery()); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "The flows index is optional."); }
        try { _approvals = [.. await Get<IApprovals>(ApprovalsKey).Pending()]; }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Approvals are optional."); }
        try { _synapses = await Get<IntoChat.OS.Modules.Apps.Synapses.ISynapseDefinitions>("synapses/definitions").List(); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Synapse catalog is optional."); }
        try { _topology = await Get<IDebuggerReads>(IDebuggerReads.DefaultKey).Topology(); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Topology read is optional."); }
        _topology = _topology with { Flows = _flows };
        await Show(State with { Status = State.Status });
    }

    private async Task InspectAsync()
    {
        _inspection = State.Selected switch
        {
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

    private async Task SyncUiNeuronsAsync(bool skipTabsConfigure = false)
    {
        try
        {
            var hops = ActiveTimelineHops;
            var steps = ReplayScript.Steps(hops);
            var stepCount = steps.Length;
            var cursor = State.ReplayCursor is { } c && stepCount > 0 ? Math.Clamp(c, 0, stepCount - 1) : (long?)null;

            if (!skipTabsConfigure && TryGetOptional<ITabs>(CreatorView.InspectorTabsKey) is { } tabs)
            {
                var currentTabs = await tabs.Read();
                if (currentTabs.Version == 0 || !string.Equals(currentTabs.SelectedId, State.InspectorTab, StringComparison.Ordinal))
                {
                    var tabsState = await tabs.Configure(
                    [
                        new UiTabItem("state", "State"),
                        new UiTabItem("source", "Source"),
                        new UiTabItem("signals", "Signals"),
                        new UiTabItem("state-graph", "State Graph"),
                        new UiTabItem("scenarios", "Scenarios"),
                    ], State.InspectorTab);
                    _lastSyncedTabsVersion = Math.Max(_lastSyncedTabsVersion, tabsState.Version);
                }
            }

            if (TryGetOptional<ISlider>(CreatorView.ReplaySliderKey) is { } slider)
            {
                var min = stepCount >= 2 ? 1d : 0d;
                var max = stepCount >= 2 ? stepCount : 1d;
                var val = stepCount == 0 ? 0d : (cursor is { } cur ? cur + 1d : stepCount);
                var sliderState = await slider.Configure(
                    ReplayScript.FormatCursorReadout(cursor, stepCount),
                    min: min,
                    max: max,
                    step: 1,
                    value: val,
                    isEnabled: stepCount > 0);
                _lastSyncedSliderVersion = sliderState.Version;
            }

            if (TryGetOptional<ICommandBar>(CreatorView.ReplayCommandBarKey) is { } transport)
            {
                var disabled = stepCount == 0;
                await transport.Configure(
                [
                    new CommandBarItem("first", "First", "chevrons-left", Disabled: disabled),
                    new CommandBarItem("prev", "Step Back", "chevron-left", Disabled: disabled),
                    new CommandBarItem("toggle", cursor is not null ? "Live" : "Replay", cursor is not null ? "play" : "pause", Disabled: disabled && cursor is null),
                    new CommandBarItem("next", "Step Forward", "chevron-right", Disabled: disabled),
                    new CommandBarItem("last", "Last", "chevrons-right", Disabled: disabled),
                ]);
            }

            if (TryGetOptional<IPropertyGrid>(CreatorView.InspectorStateKey) is { } propGrid)
            {
                var stateJson = ReplayScript.StateJsonAt(steps, cursor, _inspection.State);
                var title = cursor is { } idx && stepCount > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"State · Event {idx + 1} / {stepCount}")
                    : (_inspection.Alias is { Length: > 0 } alias ? $"Live State · {alias}" : "Neuron State");
                await propGrid.LoadJson(title, stateJson, readOnly: true);
            }

            if (TryGetOptional<ICodeEditor>(CreatorView.InspectorSourceKey) is { } editor)
            {
                var ordered = CreatorInspector.OrderedFiles(_inspection.Files);
                var activeFile = CreatorInspector.ResolveActiveFile(State.SelectedSourceFile, ordered);
                if (activeFile is not null && _inspection.Files.TryGetValue(activeFile, out var content))
                {
                    await editor.Configure(activeFile, CreatorInspector.InferLanguage(activeFile), content, readOnly: true, wordWrap: false);
                }
                else
                {
                    await editor.Configure("NoUnitSelected.cs", "csharp", "// Select an installed unit to inspect its C# source.", readOnly: true, wordWrap: false);
                }
            }

            if (TryGetOptional<ITable>(CreatorView.InspectorSignalsKey) is { } signalsTable)
            {
                if (_inspection.Kind == Selection.Edge && _inspection.Signals.Count > 0)
                {
                    await signalsTable.Render(
                        $"Signals · {_inspection.Alias ?? _inspection.Id}",
                        [
                            new TableColumn("seq", "#"),
                            new TableColumn("at", "At"),
                            new TableColumn("publisher", "Publisher"),
                            new TableColumn("trace", "Trace"),
                        ],
                        [.. _inspection.Signals.Select(s => (IReadOnlyList<string>)
                        [
                            s.Sequence.ToString(CultureInfo.InvariantCulture),
                            s.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                            s.Publisher,
                            s.TraceId ?? string.Empty,
                        ])]);
                }
                else if (hops.Count > 0)
                {
                    var firstAt = hops.Min(h => h.At);
                    var currentSeq = cursor is { } idx ? steps[idx].Sequence : (long?)null;
                    await signalsTable.Render(
                        State.TraceId is { Length: > 0 } tid ? $"Trace {tid}" : "Causal Signal Stream",
                        [
                            new TableColumn("seq", "#"),
                            new TableColumn("dt", "Δt"),
                            new TableColumn("publisher", "Publisher"),
                            new TableColumn("signal", "Signal"),
                            new TableColumn("cause", "Cause"),
                        ],
                        [.. hops.Select(h => (IReadOnlyList<string>)
                        [
                            (h.Sequence == currentSeq ? "▶ " : "") + h.Sequence.ToString(CultureInfo.InvariantCulture),
                            string.Create(CultureInfo.InvariantCulture, $"+{(h.At - firstAt).TotalMilliseconds:0}ms"),
                            TraceFold.Indent(h.Depth) + h.Publisher,
                            h.Alias,
                            h.Cause ?? string.Empty,
                        ])]);
                }
                else
                {
                    await signalsTable.Render(
                        "Causal Signal Stream",
                        [new TableColumn("seq", "#"), new TableColumn("signal", "Signal"), new TableColumn("publisher", "Publisher")],
                        []);
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Log.LogDebug(error, "Creator UI neuron sync skipped in minimal test host.");
        }
    }

    private async Task<Inspection> NodeAsync(string id)
    {
        var alias = UnitAlias(id);
        if (alias is null)
        {
            return new Inspection(Selection.Node, id)
            {
                Notice = AppRef.TryParse(id, out _) ? "A compiled app." : "A kernel neuron.",
            };
        }

        var reads = Get<IDebuggerReads>(IDebuggerReads.DefaultKey);
        string? state = null, view = null, notice = null;
        if (!id.StartsWith("draft:", StringComparison.Ordinal))
        {
            try
            {
                state = await reads.State(alias);
                view = await reads.View(alias);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "State read is optional.");
                notice = $"state unavailable: {error.Message}";
            }
        }

        return new Inspection(Selection.Node, id)
        {
            Alias = alias,
            State = state,
            View = view,
            Files = await FilesAsync(alias),
            Notice = notice,
        };
    }

    private async Task<Inspection> EdgeAsync(string key)
    {
        if (BrainGraph.ParseEdge(key) is not { } edge)
        {
            return new Inspection(Selection.Edge, key) { Notice = "Not an edge of the graph." };
        }

        var alias = edge.Alias.Split('#')[0];
        try
        {
            var page = await Get<IDebuggerReads>(IDebuggerReads.DefaultKey).Signals(alias, edge.From == "*" ? null : edge.From, EdgeSignals);
            var edgeHops = TraceFold.Fold(page.Entries);
            return new Inspection(Selection.Edge, key)
            {
                Alias = alias,
                Signals = page.Entries,
                Hops = edgeHops,
                Notice = page.Entries.Length == 0 ? $"none in the last {IDebuggerReads.Window} signals" : null,
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Journal read is optional.");
            return new Inspection(Selection.Edge, key) { Alias = alias, Notice = $"journal unavailable: {error.Message}" };
        }
    }

    private async Task<Inspection> PulseAsync(string traceId)
    {
        var entries = new List<TraceEntry>();
        try
        {
            long since = 0;
            while (entries.Count < MaxHops)
            {
                var page = await Get<ITraceReader>(ITraceReader.DefaultKey).ReadTrace(traceId, since, DigitalBrain.Kernel.SignalPage.MaxLimit);
                entries.AddRange(page.Entries);
                if (page.Entries.Length < DigitalBrain.Kernel.SignalPage.MaxLimit) { break; }
                since = page.Entries[^1].Sequence;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Trace reader is optional.");
            return new Inspection(Selection.Pulse, traceId) { Notice = $"trace reader unavailable: {error.Message}" };
        }

        var hops = TraceFold.Fold([.. entries.Take(MaxHops)]);
        return new Inspection(Selection.Pulse, traceId)
        {
            Hops = hops,
            Notice = hops.Length == 0 ? $"no journal entries carry trace {traceId}" : null,
        };
    }

    private async Task<Dictionary<string, string>> FilesAsync(string alias)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var studio = Get<ISynapseStudio>(SynapsesKey);
            foreach (var (path, text) in await studio.SourceFiles(alias)) { files[path] = text; }
            foreach (var (path, text) in await studio.Sidecars(alias)) { files[path] = text; }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Unit files are optional.");
        }

        if (files.Count == 0 && !string.IsNullOrWhiteSpace(TryBrainIdValue()) && AppRef.TryOf(Id.Brain, alias, out var appRef))
        {
            try
            {
                var exported = await Get<IApp>(appRef.Key).Export();
                if (exported?.Files is { Count: > 0 })
                {
                    foreach (var (path, text) in exported.Files)
                    {
                        files[path] = text;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "Export fallback for installed app bundle failed.");
            }
        }

        return files;
    }

    private bool Known(Selection selection) => selection.Kind switch
    {
        Selection.Pulse => true,
        Selection.Edge => _topology.Edges.Any(e => BrainGraph.EdgeKey(e.SourceId, e.TargetId, BrainGraph.EdgeLabel(e.SignalType, e.Kind)) == selection.Id),
        _ when selection.Id.StartsWith("draft:", StringComparison.Ordinal) => _flows.Any(f => f.Type == BuildFlow.Alias && f.Subject == selection.Id["draft:".Length..]),
        _ => _topology.Nodes.Any(n => n.Id == selection.Id),
    };

    private string? SelectedAlias() => State.Selected is { Kind: Selection.Node } node ? UnitAlias(node.Id) : null;

    public static string? UnitAlias(string id)
        => id.StartsWith("draft:", StringComparison.Ordinal) ? id["draft:".Length..] : BrainGraph.AliasOf(id);

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        {
            Log.LogDebug(error, "{Service} is optional when CreatorApp runs in minimal test hosts.", typeof(T).Name);
            return null;
        }
    }

    private async Task TryFollowOptionalAsync<TNeuron, TSignal>(string key)
        where TNeuron : class
        where TSignal : DigitalBrain.Signal
    {
        try { await Follow<TNeuron, TSignal>(key); }
#pragma warning disable CA1031
        catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Log.LogDebug(error, "Following {Neuron} ({Key}) is optional.", typeof(TNeuron).Name, key);
        }
    }

    private static bool MatchesNeuronKey(string sourceId, string expectedKey)
        => string.Equals(sourceId, expectedKey, StringComparison.Ordinal)
            || sourceId.EndsWith("/" + expectedKey, StringComparison.Ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
