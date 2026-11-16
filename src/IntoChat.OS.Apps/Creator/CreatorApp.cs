/*
# Creator
Role: Default
Shipped: true
Operations: open, select, trace, replay, change, scenario, make, share, seed

The one place to make software and watch it run. Left: the describe/change dialogue, each turn linked to its build flow on the
Tasks board and the consent its install waits on. Centre: the brain as a graph (units, compiled apps, ghosts of units under
construction) with live pulses from the journal tail. Right: the inspector for the selected node, edge or pulse: a unit's live
state, view, source, spec and scenarios, its needs and consent, "change it" and "run scenario"; an edge's recent signals; a
pulse's whole trace as a causation tree. Everything is read from the kernel; nothing is synthesised.
*/
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.Flutter.Fields;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Creator;

[DigitalBrain.Signal("creator.open")]
public sealed record CreatorOpen(string? Input = null) : DigitalBrain.Signal;

// Key is a graph key (node:<id>, edge:<from>→<to>:<alias>, pulse:<traceId>) or a bare node id; Value is the key the renderer sends
// through BrainGraph.SelectKey for an edge or a pulse. Neither clears the selection.
[DigitalBrain.Signal("creator.select")]
public sealed record CreatorSelect(string? Key = null, string? Value = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.trace")]
public sealed record CreatorTrace(string? TraceId = null) : DigitalBrain.Signal;

// The replay scrubber: first, prev, next, last, a step index of the open trace's signals in sequence order, or stop to go live again.
[DigitalBrain.Signal("creator.replay")]
public sealed record CreatorReplay(string? Cursor = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.change")]
public sealed record CreatorChange(string? Alias = null, string? Request = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.scenario")]
public sealed record CreatorScenario(string? Alias = null, string? Name = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.make")]
public sealed record CreatorMake(string? Goal = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.share")]
public sealed record CreatorShare(string? Alias = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.seed")]
public sealed record CreatorSeed(string? Alias = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.run")]
public sealed record CreatorRun(string? RunId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.approve")]
public sealed record CreatorApprove(string? ApprovalId = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("creator.reject")]
public sealed record CreatorReject(string? ApprovalId = null) : DigitalBrain.Signal;


[Neuron("app.creator")]
public sealed class CreatorApp : Screen<CreatorState>
{
    private static readonly ILoggerFactory ConsoleLoggerFactory = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Debug).AddConsole());
    private static readonly ILogger ConsoleLogger = ConsoleLoggerFactory.CreateLogger<CreatorApp>();

    private readonly ILogger _logger;
    private readonly TimeProvider? _time;

    public CreatorApp() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public CreatorApp(ILogger<CreatorApp>? logger = null, TimeProvider? time = null)
    {
        _logger = (ILogger?)logger ?? ConsoleLogger;
        _time = time;
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
    private Inspection _inspection = Inspection.None;
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    public TimeSpan ScenarioBudget { get; set; } = TimeSpan.FromSeconds(30);

    public Func<string, IReadOnlyDictionary<string, string>, string?, CancellationToken, Task<string>> RunScenarios { get; set; } = ScenarioTranscripts.Run;

    public const string AppName = ShippedApps.Creator;

    public static AppBundle Bundle { get; } = new(
        "creator",
        "2.0.0",
        new AppManifest(
            Title: "Creator",
            Description: "Make units and watch them run: the brain as a graph with live signals, and an inspector for state, source and traces.",
            Operations:
            [
                new("open", "Show the brain; input trace/{traceId} opens that trace, node:{id} selects that node."),
                new("select", "Inspect a node, an edge or a pulse of the graph."),
                new("trace", "Inspect a whole trace by its id."),
                new("replay", "Step the replay of the open trace: first, prev, next, last, a step index, or stop."),
                new("change", "Start a build flow that changes a unit over its current files."),
                new("scenario", "Run a unit's kept scenarios on fakes and show the transcript."),
                new("make", "Describe a new unit; Ino's studio starts its build flow."),
                new("share", "Publish the inspected unit as an interactive App-Post on your profile."),
                new("seed", "Deploy a shipped unit template into the brain."),
                new("run", "Inspect an active change run pipeline and unified diff."),
                new("approve", "Approve and promote a pending change run."),
                new("reject", "Reject and abort a pending change run."),
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
            await Show(State.SetCanvasMode(raw["mode:".Length..]));
            return;
        }
        if (raw is not null && raw.StartsWith("viewport:", StringComparison.Ordinal))
        {
            await Show(State.SetPreviewViewport(raw["viewport:".Length..]));
            return;
        }
        var selection = Selection.Parse(raw);
        if (_topology.Nodes.Count == 0) { await RefreshAsync(); }
        if (selection is not null && !Known(selection))
        {
            _inspection = new Inspection(selection.Kind, selection.Id) { Notice = "Not in the brain graph." };
            await Show(State with { Selected = selection, Transcript = null });
            return;
        }
        await Show(State.SelectSelection(selection));
        await InspectAsync();
    }

    [DigitalBrain.Neurons.On]
    public Task Trace(CreatorTrace request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Blank(request.TraceId) is { } traceId ? Select(new CreatorSelect(BrainGraph.PulseKey(traceId))) : Task.CompletedTask;
    }

    [DigitalBrain.Neurons.On]
    public async Task Replay(CreatorReplay request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var steps = State.Selected is { Kind: Selection.Pulse } ? _inspection.Hops.Count : 0;
        await Show(State.StepReplay(request.Cursor, steps));
    }

    [DigitalBrain.Neurons.On]
    public async Task Change(CreatorChange request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((Blank(request.Alias) ?? SelectedAlias()) is not { } alias) { return; }
        var wanted = Blank(request.Request) ?? Blank(State.ChangeDraft);
        if (wanted is null) { await Show(State with { Status = $"Say how {alias} should change." }); return; }
        var goal = $"change {alias}: {wanted}";
        try
        {
            var existing = await FilesAsync(alias);
            var inner = JsonSerializer.Serialize(new BuildInner { Goal = goal, Existing = existing.Count > 0 ? existing : null }, JsonSerializerOptions.Web);
            var flowId = await Get<IFlows>(FlowsKey).Start(BuildFlow.Alias, Title(goal), null, inner);
            await Show(State with { Status = $"Changing {alias} ({flowId})", ChangeDraft = "" });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "The change could not start; the status line reports why.");
            await Show(State with { Status = $"Could not change {alias}: {error.Message}" });
        }
        await RefreshAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Scenario(CreatorScenario request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((Blank(request.Alias) ?? SelectedAlias()) is not { } alias) { return; }
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
        await Show(State with { Transcript = transcript, Status = $"Ran the scenarios of {alias}" });
    }

    [DigitalBrain.Neurons.On]
    public async Task Make(CreatorMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((Blank(request.Goal) ?? Blank(State.Draft)) is not { } goal) { await Show(State with { Status = "Describe what to make." }); return; }
        if (await IsCreatorBudgetExceededAsync())
        {
            await Show(State with { Status = "Monthly compute cap exceeded. Increase cap in Settings -> Billing (os://window/settings?section=billing)." });
            return;
        }
        try
        {
            var result = await Get<IInoStudio>("ino/studio").Make(goal);
            await Show(State with { Status = result.Text, Draft = "" });
            await RecordCreatorComputeAsync(Math.Max(2_500L, goal.Length * 50L));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Ino's studio is optional; the status line reports why.");
            await Show(State with { Status = $"Could not make it: {error.Message}" });
        }
        await RefreshAsync();
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
            var brainId = State.Selected is { Kind: Selection.Node, Id: { Length: > 0 } nodeId } && nodeId.IndexOf('/', StringComparison.Ordinal) is > 0 and var slash
                ? nodeId[..slash]
                : TryBrainIdValue();
            var post = new ProfilePostItem(
                Id: $"post-{alias}",
                Content: summary,
                CreatedAt: NowUtc(),
                Circle: "public",
                Title: alias,
                TemplateAppId: alias,
                TemplateSource: csSource,
                EmbeddedApps: [new("main", alias, alias, $"{brainId}/{alias}")],
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
    public async Task Seed(CreatorSeed request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Blank(request.Alias) is not { } alias || !ShippedUnits.IsKnown(alias))
        {
            await Show(State with { Status = $"Choose a shipped unit: {string.Join(", ", ShippedUnits.All)}" });
            return;
        }

        try
        {
            var files = ShippedUnits.Files(alias);
            var deployed = await Get<ISynapseStudio>(SynapsesKey).DeployFiles(alias, files, new Dictionary<string, string>(StringComparer.Ordinal));
            await Show(State with
            {
                Status = deployed.Ok
                    ? $"Deployed shipped unit {alias} ({ShippedUnits.Title(alias)})"
                    : $"Could not deploy {alias}: {deployed.Text}",
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Deploying a shipped unit requires the synapse studio neuron.");
            await Show(State with { Status = $"Could not deploy {alias}: {error.Message}" });
        }
        await RefreshAsync();
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

    public async Task OnField(string field)
    {
        var value = (await Get<IField>(CreatorView.FieldKey(field)).Read()).Value ?? "";
        await Show(field == CreatorView.ChangeField ? State.SetChangeDraft(value) : State.SetDraft(value));
    }

    public new CreatorState State => base.State;
    public View Render() => Render(State);
    protected override View Render(CreatorState state) => CreatorView.Render(state, _topology, _flows, _approvals, _inspection);

    private async Task RefreshAsync()
    {
        try { _flows = await Get<IFlows>(FlowsKey).List(new FlowQuery()); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "The flows index is optional; the dialogue shows the last read."); }
        try { _approvals = [.. await Get<IApprovals>(ApprovalsKey).Pending()]; }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Approvals are optional when the inbox is not reachable."); }
        try { _topology = await Get<IDebuggerReads>(IDebuggerReads.DefaultKey).Topology(); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "The topology read is optional; the graph shows the last read."); }
        _topology = _topology with { Flows = _flows };
        if (string.IsNullOrEmpty(State.ConversationId))
        {
            try
            {
                if ((await Get<IInoConversations>(IInoConversations.DefaultKey).Current()).FirstOrDefault() is { } agent) { await Show(State with { ConversationId = agent }); }
            }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "The Ino conversation is optional; the dialogue shows the build flows."); }
        }
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
        await Show(State with { Status = State.Status });
    }

    private async Task<Inspection> NodeAsync(string id)
    {
        var alias = UnitAlias(id);
        if (alias is null) { return new Inspection(Selection.Node, id) { Notice = id.Contains("/apps/", StringComparison.Ordinal) ? "A compiled app." : "A kernel neuron." }; }
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
                Log.LogDebug(error, "The state read is optional; the inspector says it is unavailable.");
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
        if (BrainGraph.ParseEdge(key) is not { } edge) { return new Inspection(Selection.Edge, key) { Notice = "Not an edge of the graph." }; }
        var alias = edge.Alias.Split('#')[0];
        try
        {
            var page = await Get<IDebuggerReads>(IDebuggerReads.DefaultKey).Signals(alias, edge.From == "*" ? null : edge.From, EdgeSignals);
            return new Inspection(Selection.Edge, key)
            {
                Alias = alias,
                Signals = page.Entries,
                Notice = page.Entries.Length == 0 ? $"none in the last {IDebuggerReads.Window} signals" : null,
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "The journal read is optional; the inspector says it is unavailable.");
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
            Log.LogDebug(error, "The trace reader is optional; the inspector says it is unavailable.");
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
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "The unit's files are optional; the inspector shows what was read."); }

        if (files.Count == 0 && !string.IsNullOrWhiteSpace(Id.Brain.Value))
        {
            try
            {
                var exported = await Get<IApp>(IApp.Key(Id.Brain.Value, alias)).Export();
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

    private async Task RecordCreatorComputeAsync(long micros)
    {
        try { await Get<ICompute>("compute").Record("creator", micros); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogDebug(error, "Compute metering is optional when the compute neuron is not registered."); }
    }

    private async Task<bool> IsCreatorBudgetExceededAsync()
    {
        try { return (await Get<ICompute>("compute").Read()).BudgetExceeded; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Compute metering is optional when the compute neuron is not registered.");
            return false;
        }
    }

    private static string Title(string goal) => goal.Length <= 52 ? goal : goal[..52];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
