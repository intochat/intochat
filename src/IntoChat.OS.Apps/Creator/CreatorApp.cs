/*
# Creator
Role: Developer
Shipped: true
Operations: open, select, trace, replay, scenario, share, run, approve, reject

Opt-in DevUI topology, Accordant state-space, scenario & 1-event-at-a-time time-travel debugger for the DigitalBrain OS.
Left: catalog units, installed apps, active build/engineer flows, and studio launchers.
Center: live BrainScene topology (with causal replay pulses and Flame camera HUD), embedded Live Studio preview,
Accordant StateGraphView, and the time-travel transport bar with a control.slider child.
Right: 5-tab inspector on a control.tabStrip child (State as a jsonTree node, Source as a code block, Signals on a control.queryTable
child, State Graph, Scenarios), drawn from the inspection Creator keeps in its stored state.
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
using IntoChat.OS.UI;
using IntoChat.OS.Modules.UI.Controls;
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

[Neuron("app.creator"), Screen("app.creator", Title = "Creator", Icon = "cpu", Category = "Developer")]
public sealed partial class CreatorApp : AggregateScreen<CreatorWorkspaceState>, INavigatedTo<StudioNavArgs>, INavigatedFrom
{
    private static readonly ILogger ConsoleLogger = IntoChat.OS.Host.Logging.OsConsoleLog.For<CreatorApp>();

    private readonly ILogger _logger;
    private readonly TimeProvider? _time;

    public CreatorApp() : this(null, null, null) { }

    [ActivatorUtilitiesConstructor]
    public CreatorApp(ILogger<CreatorApp>? logger = null, TimeProvider? time = null, IUnitVerifier? verifier = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
        _time = time;
        RunScenarios = verifier is null ? NoScenarioRuns : verifier.Transcript;
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
        await FollowChild<ITabStripControl, DigitalBrain.UI.Commands.SelectionChanged>(CreatorView.GraphModeKey);
        await FollowChild<ITabStripControl, DigitalBrain.UI.Commands.SelectionChanged>(CreatorView.ViewportKey);
        await FollowChild<ITabStripControl, DigitalBrain.UI.Commands.SelectionChanged>(CreatorView.InspectorTabsKey);
        await FollowChild<ISliderControl, DigitalBrain.UI.Commands.ValueChanged>(CreatorView.ReplaySliderKey);
        await FollowChild<IQueryTableControl, DigitalBrain.UI.Commands.RowSelected>(CreatorView.InspectorSignalsKey);

        await RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public Task RunsChanged(IntoChat.OS.Modules.Apps.Runs.RunsChanged? changed)
        => changed is null ? Task.CompletedTask : RefreshAsync();

    // The graph mode, the preview width and the inspector tab are tab strip children that own nothing: Creator holds all three and
    // draws them as props.
    [DigitalBrain.Neurons.On]
    public async Task OnSelectionChanged(DigitalBrain.UI.Commands.SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        switch (SignalChild)
        {
            case CreatorView.GraphModeKey:
                await ApplyDecisionAsync(State.SwitchCanvasMode(changed.Id));
                break;
            case CreatorView.ViewportKey:
                await Show(State.SetPreviewViewport(changed.Id));
                break;
            case CreatorView.InspectorTabsKey:
                await ApplyDecisionAsync(State.SelectInspectorTab(changed.Id));
                break;
        }
    }

    // The replay slider draws Creator's cursor: step n of the timeline is value n. Letting go moves the cursor to that step.
    [DigitalBrain.Neurons.On]
    public async Task OnReplayScrubbed(DigitalBrain.UI.Commands.ValueChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var steps = ReplayScript.Steps(ActiveTimelineHops);
        if (steps.Length == 0)
        {
            return;
        }

        var index = Math.Clamp((long)Math.Round(changed.Value) - 1L, 0L, steps.Length - 1L);
        if (State.ReplayCursor != index)
        {
            await ApplyDecisionAsync(State.StepTimeline(index.ToString(CultureInfo.InvariantCulture), steps.Length));
        }
    }

    // A row of the signals table is a journal sequence: a timeline hop steps the cursor to it; an edge signal of another trace opens it.
    [DigitalBrain.Neurons.On]
    public async Task OnSignalRowSelected(DigitalBrain.UI.Commands.RowSelected selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (!long.TryParse(selected.RowId, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return;
        }

        var steps = ReplayScript.Steps(ActiveTimelineHops);
        var stepIndex = Array.FindIndex(steps, s => s.Sequence == sequence);
        if (stepIndex >= 0)
        {
            if (State.ReplayCursor != stepIndex)
            {
                await ApplyDecisionAsync(State.StepTimeline(stepIndex.ToString(CultureInfo.InvariantCulture), steps.Length));
            }

            return;
        }

        if (State.Inspection is { Kind: Selection.Edge } edge
            && Array.Find(edge.Signals, s => s.Sequence == sequence) is { TraceId: { Length: > 0 } traceId })
        {
            await Trace(new CreatorTrace(traceId));
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
    [Slot(Required = false)] private partial IntoChat.OS.Modules.Apps.Social.IPosts? Posts { get; }
    [Slot(Key = IConfirmations.ConfirmationsNeuronKey, Required = false)] private partial IConfirmationInbox? ConfirmationInbox { get; }
    [Slot(Required = false)] private partial SlotCollection<IntoChat.OS.Modules.AI.Workflows.IWorkflowRun> WorkflowRuns { get; }
    [Slot(Required = false)] private partial SlotCollection<IApp> Apps { get; }
    public const int EdgeSignals = 20;
    public const int MaxHops = 2000;

    private BrainTopology _topology = new([], []);
    private FlowView[] _flows = [];
    private Approval[] _approvals = [];
    private IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition[] _synapses = [];
    private DigitalBrain.Units.DeployedSource[] _deployedUnits = [];
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    private TraceHop[] ActiveTimelineHops => State.Inspection.Hops;

    public TimeSpan ScenarioBudget { get; set; } = TimeSpan.FromSeconds(30);

    // The host's unit verifier runs the scenarios; a host without one says so.
    public Func<string, IReadOnlyDictionary<string, string>, string?, CancellationToken, Task<string>> RunScenarios { get; set; }

    private static Task<string> NoScenarioRuns(string alias, IReadOnlyDictionary<string, string> files, string? scenario, CancellationToken ct)
        => Task.FromResult("Scenario runs are not available in this brain.");

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
    }

    [DigitalBrain.Neurons.On]
    public async Task Select(CreatorSelect request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = Blank(request.Value) ?? Blank(request.Key);
        if (string.Equals(raw, CreatorView.ToggleStateGraphTarget, StringComparison.Ordinal))
        {
            await Show(State.ToggleStateGraph());
            return;
        }

        if (raw is not null && raw.StartsWith("mode:", StringComparison.Ordinal))
        {
            await ApplyDecisionAsync(State.SwitchCanvasMode(raw["mode:".Length..]));
            return;
        }

        if (raw is not null && raw.StartsWith("tab:", StringComparison.Ordinal))
        {
            await ApplyDecisionAsync(State.SelectInspectorTab(raw["tab:".Length..]));
            return;
        }

        if (raw is not null && raw.StartsWith("file:", StringComparison.Ordinal))
        {
            await Show(State.SetSourceFile(raw["file:".Length..]));
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
            await Show(State with
            {
                Selected = selection,
                Transcript = null,
                Inspection = new Inspection(selection.Kind, selection.Id) { Notice = "Not in the brain graph.", Hops = ActiveTimelineHops },
            });
            return;
        }

        if (selection is null)
        {
            await Show(State.ClearSelection());
            return;
        }

        await Show(State.SelectSelection(selection, ActiveTimelineHops.Length));
        await InspectAsync();
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

        await Show(State with
        {
            Inspection = State.Inspection with
            {
                ScenarioPassRate = CreatorStateSpaceInspector.ComputeTranscriptPassRate(transcript) ?? State.Inspection.ScenarioPassRate,
            },
            Transcript = transcript,
            InspectorTab = "scenarios",
            Status = $"Ran the scenarios of {alias}",
        });
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
            if (Posts is not { } posts)
            {
                await Show(State with { Status = $"Could not share {alias}: the post store is unavailable." });
                return;
            }

            // The post goes to the brain's post store for its owner only, as it was when the profile kept it: it carries
            // the unit's source, and showing it to friends is a choice made in the Friends app.
            await posts.Publish(new IntoChat.OS.Modules.Apps.Social.PostDraft(
                summary,
                [],
                new IntoChat.OS.Modules.Apps.Social.AppDefinitionShare(
                    alias,
                    alias,
                    [.. files.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => new IntoChat.OS.Modules.Apps.Social.SharedSourceFile(kv.Key, kv.Value))]),
                Id: $"post-{alias}",
                Title: alias,
                Audience: new Audience.Only([]),
                EmbeddedApps: [new("main", alias, alias, embeddedTarget)],
                CreatedAt: NowUtc(),
                TemplateSource: files.Keys.Any(static path => path.EndsWith(".cs", StringComparison.Ordinal)) || string.IsNullOrWhiteSpace(csSource) ? null : csSource));
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
        => CreatorView.RenderNode(state, _topology, _flows, _approvals, state.Inspection, state.Inspection.Hops, _synapses, ActiveTransition, _deployedUnits,
            new CreatorView.Children(Child<ITabStripControl, TabStripProps>, Child<ISliderControl, SliderProps>, Child<IQueryTableControl, QueryTableProps>));

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

    // Reads the selection into the stored inspection. A selection without a trace of its own keeps the last timeline.
    private async Task InspectAsync()
    {
        var inspection = State.Selected switch
        {
            null when State.ActiveRunId is { Length: > 0 } runId => await FlowRunAsync(runId),
            null => Inspection.None,
            { Kind: Selection.Pulse } pulse => await PulseAsync(pulse.Id),
            { Kind: Selection.Edge } edge => await EdgeAsync(edge.Id),
            { } node => await NodeAsync(node.Id),
        };

        if (inspection.Hops.Length == 0)
        {
            inspection = inspection with { Hops = ActiveTimelineHops };
        }

        await Show(State with { Inspection = inspection, ReplayStepCount = ReplayScript.Steps(inspection.Hops).Length });
    }

    private Task TryFollowOptionalAsync<TNeuron, TSignal>(string key)
        where TNeuron : class
        where TSignal : DigitalBrain.Signal
        => FollowOptionalAsync<TNeuron, TSignal>(key, Log);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
