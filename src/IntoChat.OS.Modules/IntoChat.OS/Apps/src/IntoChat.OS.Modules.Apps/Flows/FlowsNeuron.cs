using System.Text.Json;
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
// handlers save. The commands find the flow neuron by id and call it, so they interleave with the handlers.
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
            .Where(item => query.Status is null || item.Status == query.Status)
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
        if (OperationOf((await flow.Head()).InnerJson) is { } operation)
        {
            // An operation that already ended (or is unknown to the brain) has nothing to cancel.
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
    public async Task OnFlowStarted(FlowStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        if (State.Flows.ContainsKey(started.FlowId)) { return; }
        FlowHead? head = null;
        try { head = await Flow(started.FlowId).Head(); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { FlowsLog.HeadUnread(Logger(), error, started.FlowId); }
        await Put(new FlowEntry(started.FlowId, started.Type, started.Title, head?.Owner ?? SystemOwner, head?.TraceId ?? "", FlowStatus.Running,
            head?.Steps ?? [], [], null, null, [], head?.ParentFlowId, head?.Started ?? Context.Time.GetUtcNow(), null, null));
    }

    [On]
    public Task OnFlowStep(FlowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Change(step.FlowId, entry => step.Outcome switch
        {
            StepOutcome.Started => entry with { Status = entry.Finished ? entry.Status : FlowStatus.Running, Current = step.Step },
            StepOutcome.Completed => entry with { Done = entry.Done.Contains(step.Step) ? entry.Done : [.. entry.Done, step.Step], Current = null },
            _ => entry with { Detail = step.Detail },
        });
    }

    [On]
    public Task OnFlowWaiting(FlowWaiting waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        return Change(waiting.FlowId, entry => entry.Finished ? entry : entry with
        {
            Status = FlowStatus.Waiting,
            Current = waiting.Step,
            Waiting = new Waiting(waiting.Step, waiting.Reason, null, waiting.ConfirmationPlanId),
        });
    }

    [On]
    public Task OnFlowResolve(FlowResolve resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return Change(resolve.FlowId, entry => entry with
        {
            Status = resolve.Outcome is WaitOutcome.Approved or WaitOutcome.Arrived && !entry.Finished ? FlowStatus.Running : entry.Status,
            Waiting = null,
            Current = null,
        });
    }

    [On]
    public Task OnFlowDone(FlowDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        return Change(done.FlowId, entry => entry with
        {
            Status = done.Outcome switch { FlowOutcome.Completed => FlowStatus.Completed, FlowOutcome.Failed => FlowStatus.Failed, _ => FlowStatus.Cancelled },
            Current = null,
            Waiting = null,
            Ended = Context.Time.GetUtcNow(),
            Detail = done.Detail ?? entry.Detail,
        });
    }

    [On]
    public Task OnFlowRelated(FlowRelated related)
    {
        ArgumentNullException.ThrowIfNull(related);
        return Change(related.FlowId, entry => entry.Relations.Contains(related.Relation) ? entry : entry with { Relations = [.. entry.Relations, related.Relation] });
    }

    private IFlowStarter Flow(string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        if (!NeuronId.TryParse(flowId, out var id) || id.Brain != Id.Brain) { throw new ArgumentException($"{flowId} is not a flow of this brain.", nameof(flowId)); }
        return Context.Get<IFlowStarter>(id);
    }

    private Task Change(string flowId, Func<FlowEntry, FlowEntry> change)
    {
        if (!State.Flows.TryGetValue(flowId, out var entry)) { return Task.CompletedTask; }
        var next = change(entry);
        return ReferenceEquals(next, entry) ? Task.CompletedTask : Put(next);
    }

    private Task Put(FlowEntry entry)
    {
        var flows = new Dictionary<string, FlowEntry>(State.Flows, StringComparer.Ordinal) { [entry.FlowId] = entry };
        var finished = flows.Values.Where(candidate => candidate.Finished).ToArray();
        if (finished.Length > FlowsState.KeepFinished)
        {
            foreach (var old in finished.OrderBy(candidate => candidate.Ended).Take(finished.Length - FlowsState.KeepFinished)) { flows.Remove(old.FlowId); }
        }
        return Save(State with { Flows = flows }, new FlowsChanged(entry.FlowId));
    }

    private async Task<FlowView> View(FlowEntry entry, IReadOnlyDictionary<string, FlowEntry> flows, CancellationToken ct)
    {
        var steps = entry.Steps.Length;
        var fraction = steps == 0 ? (entry.Status == FlowStatus.Completed ? 1d : 0d) : (double)entry.Done.Length / steps;
        if (steps > 0 && entry is { Status: FlowStatus.Running, Current: not null })
        {
            try
            {
                if (OperationOf((await Flow(entry.FlowId).Head()).InnerJson) is { } operation
                    && await Context.OperationsQuery.Read(operation, ct) is { Result: null } running)
                {
                    fraction += Math.Clamp(running.Fraction, 0, 1) / steps;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { FlowsLog.HeadUnread(Logger(), error, entry.FlowId); }
        }
        return new FlowView(entry.FlowId, entry.Type, entry.Title, entry.Owner, entry.TraceId, StatusOf(entry, flows), entry.Steps, entry.Done,
            entry.Current, entry.Waiting?.Reason, entry.Waiting?.Step, entry.Waiting?.ConfirmationPlanId, Math.Min(1d, fraction), entry.Relations,
            [.. BlockersOf(entry, flows)], entry.ParentFlowId, entry.Started, entry.Ended, entry.Detail);
    }

    private static FlowStatus StatusOf(FlowEntry entry, IReadOnlyDictionary<string, FlowEntry> flows)
        => BlockersOf(entry, flows).Any() ? FlowStatus.Blocked : entry.Status;

    // The BlockedBy targets of a Pending or Running flow that have not completed; a target the index does not know counts as open.
    private static IEnumerable<string> BlockersOf(FlowEntry entry, IReadOnlyDictionary<string, FlowEntry> flows)
        => entry.Status is FlowStatus.Pending or FlowStatus.Running
            ? entry.Relations.Where(relation => relation.Kind == RelationKind.BlockedBy
                    && !(flows.TryGetValue(relation.TargetFlowId, out var target) && target.Status == FlowStatus.Completed))
                .Select(relation => relation.TargetFlowId).Distinct(StringComparer.Ordinal)
            : [];

    private static OperationId? OperationOf(string innerJson)
    {
        try
        {
            using var document = JsonDocument.Parse(innerJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("operationId", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } id
                ? new OperationId(id)
                : null;
        }
        catch (JsonException) { return null; }
    }

    private ILogger Logger() => Context.Services.GetService<ILoggerFactory>()?.CreateLogger<FlowsNeuron>() ?? (ILogger)NullLogger.Instance;

    private static string Caller()
        => RuntimeCallerContext.TryGet(out var caller) && !string.IsNullOrWhiteSpace(caller.PrincipalId) ? caller.PrincipalId : SystemOwner;
}

internal static partial class FlowsLog
{
    // The index shows the flow without its steps, owner or progress.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The flows index could not read flow {FlowId}")]
    public static partial void HeadUnread(ILogger logger, Exception exception, string flowId);
}
