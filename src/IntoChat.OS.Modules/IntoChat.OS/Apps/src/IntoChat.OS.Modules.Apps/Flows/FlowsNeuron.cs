using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Flows.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Modules.Apps.Flows;

// The brain's flows index. It holds a brain-wide synapse on the six flow.* signals and folds each into its entry; only those
// handlers save. A signal for a flow the index does not know yet makes a placeholder entry; the flow's FlowHead fills it in when
// it can be read (retried on the flow's next signals, at most FlowEntry.MaxHeadAttempts times, never on reads; once finished only by
// the FlowDone of a flow whose head is still unknown). Signals are folded idempotently: a repeated
// or stale one does not move an entry back. The commands find the flow neuron by id and call it, so they interleave.
[DigitalBrain.Neuron("flows")]
public sealed class FlowsNeuron : DigitalBrain.Neuron<FlowsState>, IFlows
{
    private const string SystemOwner = "system";

    protected override Task OnActivate(CancellationToken cancellationToken)
        => Context.Synapses.EnsureBrainWide(typeof(FlowStarted), typeof(FlowStep), typeof(FlowWaiting), typeof(FlowResolve), typeof(FlowDone), typeof(FlowRelated));

    public async Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var flows = State.Flows;
        var matches = flows.Values
            .Where(entry => (query.Owner is null || entry.Owner == query.Owner)
                && (query.Type is null || entry.Type == query.Type)
                && (query.ParentFlowId is null || entry.ParentFlowId == query.ParentFlowId))
            .Select(entry => (Entry: entry, Status: StatusOf(entry, flows)))
            .Where(item => (query.Status is null || item.Status == query.Status) && (query.AnyStatus is null || query.AnyStatus.Contains(item.Status)))
            .OrderByDescending(item => item.Entry.Started)
            .Take(Math.Max(0, query.Limit))
            .ToArray();
        var views = new FlowView[matches.Length];
        for (var i = 0; i < matches.Length; i++) { views[i] = await View(matches[i].Entry, flows, ct); }
        return views;
    }

    public async Task<FlowView?> Read(string flowId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        var flows = State.Flows;
        return flows.TryGetValue(flowId, out var entry) ? await View(entry, flows, ct) : null;
    }

    public async Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(innerJson);
        var blocker = parentFlowId is not null && State.Flows.TryGetValue(parentFlowId, out var parent) ? BlockersOf(parent, State.Flows).FirstOrDefault() : null;
        await Check("Start", new ParentNotBlocked(blocker));
        var id = new NeuronId(Id.Brain, type, Guid.NewGuid().ToString("N"));
        await Context.Get<IFlowStarter>(id).StartFromJson(title, Caller(), parentFlowId, innerJson);
        return id.ToString();
    }

    public Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        return Flow(flowId).Resume(step, outcome, Caller());
    }

    public async Task Cancel(string flowId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var flow = Flow(flowId);
        if ((await flow.Head()).OperationId is { Length: > 0 } id)
        {
            // An operation that already ended (or is unknown to the brain) has nothing to cancel.
            var operation = new OperationId(id);
            if (await Context.OperationsQuery.Read(operation, ct) is { Result: null }) { await Context.Operations.Cancel(operation, ct); }
        }
        await flow.Cancel(reason);
    }

    public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        return Flow(flowId).Relate(relation);
    }

    [On]
    public Task OnFlowStarted(FlowStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        return Fold(started.FlowId, entry => entry with
        {
            Type = started.Type,
            Title = started.Title,
            Status = entry.Status == FlowStatus.Pending ? FlowStatus.Running : entry.Status,
        }, readHead: true);
    }

    [On]
    public Task OnFlowStep(FlowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Outcome switch
        {
            // A start redelivered after the step completed or the flow ended is stale.
            StepOutcome.Started => Fold(step.FlowId, entry => entry.Finished || entry.Done.Contains(step.Step) ? entry
                : entry with { Status = FlowStatus.Running, Current = step.Step, OperationId = null, ResolvedStep = entry.ResolvedStep == step.Step ? entry.ResolvedStep : null },
                readHead: true),
            StepOutcome.Completed => Fold(step.FlowId, entry => entry.Done.Contains(step.Step) ? entry : entry with
            {
                Done = [.. entry.Done, step.Step],
                Status = entry.Status == FlowStatus.Pending ? FlowStatus.Running : entry.Status,
                Current = entry.Current == step.Step ? null : entry.Current,
                OperationId = entry.Current == step.Step ? null : entry.OperationId,
            }),
            _ => Fold(step.FlowId, entry => entry.Detail == step.Detail ? entry : entry with { Detail = step.Detail }),
        };
    }

    [On]
    public Task OnFlowWaiting(FlowWaiting waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        return Fold(waiting.FlowId, entry => entry.Finished || entry.Done.Contains(waiting.Step) || entry.ResolvedStep == waiting.Step ? entry : entry with
        {
            Status = FlowStatus.Waiting,
            Current = waiting.Step,
            Waiting = new Waiting(waiting.Step, waiting.Reason, null, waiting.ConfirmationPlanId),
        }, readHead: true);
    }

    [On]
    public Task OnFlowResolve(FlowResolve resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        // Only the wait the entry shows is ended; a late resolve of an older wait is stale.
        // A resolve that arrives before its FlowWaiting is remembered, so that FlowWaiting does not leave the entry waiting.
        return Fold(resolve.FlowId, entry => entry.Waiting is null ? (entry.ResolvedStep == resolve.Step ? entry : entry with { ResolvedStep = resolve.Step })
            : entry.Waiting.Step != resolve.Step ? entry : entry with
        {
            ResolvedStep = resolve.Step,
            Status = resolve.Outcome is WaitOutcome.Approved or WaitOutcome.Arrived && !entry.Finished ? FlowStatus.Running : entry.Status,
            Waiting = null,
            Current = null,
        });
    }

    [On]
    public Task OnFlowDone(FlowDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        return Fold(done.FlowId, entry => entry.Finished ? entry : entry with
        {
            Status = done.Outcome switch { FlowOutcome.Completed => FlowStatus.Completed, FlowOutcome.Failed => FlowStatus.Failed, _ => FlowStatus.Cancelled },
            Current = null,
            Waiting = null,
            OperationId = null,
            Ended = Context.Time.GetUtcNow(),
            Detail = done.Detail ?? entry.Detail,
        }, readFinished: true);
    }

    [On]
    public Task OnFlowRelated(FlowRelated related)
    {
        ArgumentNullException.ThrowIfNull(related);
        return Fold(related.FlowId, entry => entry.Relations.Contains(related.Relation) ? entry : entry with { Relations = [.. entry.Relations, related.Relation] });
    }

    private IFlowStarter Flow(string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        if (!NeuronId.TryParse(flowId, out var id) || id.Brain != Id.Brain) { throw new ArgumentException($"{flowId} is not a flow of this brain.", nameof(flowId)); }
        return Context.Get<IFlowStarter>(id);
    }

    // Applies change to the flow's entry (a placeholder when the index has none) and saves it when it changed. The flow's head is
    // read when readHead asks for it or the entry has none yet.
    private async Task Fold(string flowId, Func<FlowEntry, FlowEntry> change, bool readHead = false, bool readFinished = false)
    {
        var known = State.Flows.TryGetValue(flowId, out var existing);
        var entry = known ? existing! : Placeholder(flowId);
        var next = change(entry);
        var wantsHead = next.Finished ? readFinished && !next.HeadKnown : readHead || !next.HeadKnown;
        if (wantsHead && next.HeadAttempts < FlowEntry.MaxHeadAttempts) { next = await WithHead(next); }
        if (known && ReferenceEquals(next, existing)) { return; }
        await Save(State.With(next), new FlowsChanged(flowId));
    }

    private FlowEntry Placeholder(string flowId)
        => new(flowId, NeuronId.TryParse(flowId, out var id) ? id.Type : "", "", SystemOwner, "", FlowStatus.Pending, [], [], null, null, [], null,
            Context.Time.GetUtcNow(), null, null);

    private async Task<FlowEntry> WithHead(FlowEntry entry)
    {
        try { return entry.Merge(await Flow(entry.FlowId).Head()); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            var attempts = entry.HeadAttempts + 1;
            if (attempts == FlowEntry.MaxHeadAttempts) { FlowsLog.HeadUnread(Logger(), error, entry.FlowId, attempts); }
            return entry with { HeadAttempts = attempts };
        }
    }

    private async Task<FlowView> View(FlowEntry entry, IReadOnlyDictionary<string, FlowEntry> flows, CancellationToken ct)
    {
        var steps = entry.Steps.Length;
        var fraction = steps == 0 ? (entry.Status == FlowStatus.Completed ? 1d : 0d) : (double)entry.Done.Length / steps;
        if (steps > 0 && entry is { Status: FlowStatus.Running or FlowStatus.Waiting, Current: not null, OperationId: { Length: > 0 } operation }
            && await Context.OperationsQuery.Read(new OperationId(operation), ct) is { Result: null } running)
        {
            fraction += Math.Clamp(running.Fraction, 0, 1) / steps;
        }
        return new FlowView(entry.FlowId, entry.Type, entry.Title, entry.Owner, entry.TraceId, StatusOf(entry, flows), entry.Steps, entry.Done,
            entry.Current, entry.Waiting?.Reason, entry.Waiting?.Step, entry.Waiting?.ConfirmationPlanId, Math.Min(1d, fraction), entry.Relations,
            [.. BlockersOf(entry, flows)], entry.ParentFlowId, entry.Started, entry.Ended, entry.Detail);
    }

    private static FlowStatus StatusOf(FlowEntry entry, IReadOnlyDictionary<string, FlowEntry> flows)
        => BlockersOf(entry, flows).Any() ? FlowStatus.Blocked : entry.Status;

    // The BlockedBy targets of a Pending or Running flow that the index knows and that have not completed. A target the index does
    // not know (never seen, or evicted once finished) does not block.
    private static IEnumerable<string> BlockersOf(FlowEntry entry, IReadOnlyDictionary<string, FlowEntry> flows)
        => entry.Status is FlowStatus.Pending or FlowStatus.Running
            ? entry.Relations.Where(relation => relation.Kind == RelationKind.BlockedBy
                    && flows.TryGetValue(relation.TargetFlowId, out var target) && target.Status != FlowStatus.Completed)
                .Select(relation => relation.TargetFlowId).Distinct(StringComparer.Ordinal)
            : [];

    private ILogger Logger() => Context.Services.GetService<ILoggerFactory>()?.CreateLogger<FlowsNeuron>() ?? (ILogger)NullLogger.Instance;

    private static string Caller()
        => RuntimeCallerContext.TryGet(out var caller) && !string.IsNullOrWhiteSpace(caller.PrincipalId) ? caller.PrincipalId : SystemOwner;
}

internal static partial class FlowsLog
{
    // Logged once, when retries stop: the index shows the flow without its steps, owner or progress.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The flows index could not read flow {FlowId} after {Attempts} attempts; it stops trying")]
    public static partial void HeadUnread(ILogger logger, Exception exception, string flowId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The flows graph source could not list the flows of brain {Brain}")]
    public static partial void GraphUnlisted(ILogger logger, Exception exception, string brain);
}
