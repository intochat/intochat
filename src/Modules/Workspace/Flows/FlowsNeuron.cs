using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.Apps.Flows.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Modules.Apps.Flows;

// The brain's flows index. It holds a brain-wide synapse on the six flow.* signals and folds each into its entry; only those
// handlers save. A signal for a flow the index does not know yet makes a placeholder entry; the flow's FlowHead fills it in when
// it can be read (retried on the flow's next signals, at most FlowEntry.MaxHeadAttempts times, never on reads; once finished only by
// the FlowDone of a flow whose head is still unknown). Signals are folded idempotently: a repeated
// or stale one does not move an entry back. The commands find the flow neuron by id and call it, so they interleave.
[DigitalBrain.Neurons.Neuron("flows")]
internal sealed class FlowsNeuron(
    IOperations operations,
    IOperationsQuery operationsQuery,
    ILogger<FlowsNeuron>? logger = null,
    IServiceProvider? services = null) : DigitalBrain.Neurons.Neuron<FlowsState>, IFlows
{
    // The app id a flow an agent starts is recorded under: Ino's flows.
    private const string AssistantApp = CallerApps.Assistant;

    private readonly SecurityInterrupts _interrupts = new();

    // Start interleaves with the signal turns, and both save the index. A save carries the version the last finished save
    // left; two in flight at once would carry the same one and the later would be refused as stale. One save at a time.
    private readonly SemaphoreSlim _saving = new(1, 1);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Context.Wires.EnsureBrainWide(typeof(FlowStarted), typeof(FlowStep), typeof(FlowWaiting), typeof(FlowResolve), typeof(FlowDone), typeof(FlowRelated), typeof(FlowSubject),
            typeof(ContextUpdated), typeof(ContextRemoved));
        await RebuildInterrupts();
    }

    // Reads the posture context and the contexts the current conversation agent lists; an agent definition is the only list of
    // the others, so an interrupt in a context neither names is seen only once it is written.
    private async Task RebuildInterrupts()
    {
        _interrupts.Clear();
        var cancel = false;
        var prefix = Id.Brain.Value + "/";
        string[] contexts = ["context/security.posture"];
        try
        {
            var current = await Context.Get<ISessionList>(new NeuronId(Id.Brain, Sessions.Type, ISessionList.DefaultKey)).Current();
            if (!string.IsNullOrWhiteSpace(current) && ConversationId.TryParse(current, out var convId))
            {
                var view = await Context.Get<IConversation>(new NeuronId(Id.Brain, IConversation.Type, IConversation.KeyOf(convId))).Read();
                contexts = [.. contexts, .. view.Contexts];
            }
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { FlowsLog.InterruptsUnread(Logger(), error, "conversation"); }
        foreach (var key in contexts.Select(key => key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var snapshot = await Context.Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", key)).Read();
                foreach (var entry in snapshot.Entries) { cancel |= _interrupts.Keep(prefix + key, entry.Name, entry); }
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { FlowsLog.InterruptsUnread(Logger(), error, key); }
        }
        if (cancel) { await CancelInoFlows(); }
    }

    // Cancels every running or waiting Ino flow; one that cannot be cancelled is logged and the rest still are. The interrupt
    // is a person's or the platform's, so a flow that waits for a person is cancelled too: this does not pass Cancel's rule.
    private async Task CancelInoFlows()
    {
        foreach (var flow in State.Flows.Values.Where(entry => entry.App == AssistantApp && entry.Status is FlowStatus.Running or FlowStatus.Waiting).ToArray())
        {
            try
            {
                var starter = Flow(flow.FlowId);
                await StopElevated(starter, flow.FlowId, await starter.Head(), "security interrupt");
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { FlowsLog.InterruptCancelFailed(Logger(), error, flow.FlowId); }
        }
    }

    [On]
    public async Task OnContextUpdated(ContextUpdated updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var prefix = Id.Brain.Value + "/";
        if (!updated.ContextId.StartsWith(prefix, StringComparison.Ordinal)) { return; }
        var cancel = false;
        try
        {
            var snapshot = await Context.Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", updated.ContextId[prefix.Length..])).Read();
            cancel = _interrupts.Keep(updated.ContextId, updated.Name, snapshot.Entries.FirstOrDefault(item => item.Name == updated.Name));
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            FlowsLog.InterruptsUnread(Logger(), error, updated.ContextId);
            return;
        }
        if (cancel) { await CancelInoFlows(); }
    }

    [On]
    public Task OnContextRemoved(ContextRemoved removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        _interrupts.Remove(removed.ContextId, removed.Name);
        return Task.CompletedTask;
    }

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

    public async Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null)
    {
        await Check("Start", new FlowKeyIsValid(flowId));
        await Check("Start", new NotUnderSecurityInterrupt(Context.Caller?.Actor is Actor.Agent ? _interrupts.Reason : null));
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(innerJson);
        var blocker = parentFlowId is not null && State.Flows.TryGetValue(parentFlowId, out var parent) ? BlockersOf(parent, State.Flows).FirstOrDefault() : null;
        var id = new NeuronId(Id.Brain, type, flowId ?? Guid.NewGuid().ToString("N"));
        var held = flowId is not null && State.Flows.TryGetValue(id.ToString(), out var existingEntry) ? existingEntry : null;
        var headStatus = flowId is not null && held is null ? (await Context.Get<IFlowStarter>(id).Head()).Status : (DigitalBrain.Flows.FlowStatus?)null;
        await Check("Start", new ParentNotBlocked(blocker), new FlowIsNew(id.ToString(), flowId, held, headStatus));
        var starterApp = StarterApp(Context.Caller);
        var conversation = Context.Caller?.Conversation;
        if (starterApp is { Length: > 0 } || conversation is { Length: > 0 })
        {
            await Fold(id.ToString(), entry => entry.App == starterApp && entry.Conversation == conversation ? entry
                : entry with { App = starterApp, Conversation = conversation }, skipHead: true);
        }
        await Context.Get<IFlowStarter>(id).StartFromJson(title, Owner(), parentFlowId, innerJson);
        return id.ToString();
    }

    public async Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        if (State.Flows.TryGetValue(flowId, out var entry))
        {
            if (entry.Finished || entry.Done.Contains(step) || entry.ResolvedStep == step || entry.ResolvedSteps.Contains(step, StringComparer.Ordinal))
            {
                return;
            }
            if (entry.Waiting is not null && !string.Equals(entry.Waiting.Step, step, StringComparison.Ordinal))
            {
                return;
            }
        }
        await Flow(flowId).Resume(step, outcome, CallerRules.ActsFor(RequiredCaller()));
    }

    public async Task Cancel(string flowId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var flow = Flow(flowId);
        var head = await flow.Head();
        await Check("Cancel", new PersonCancelsApprovalWait(flowId, PersonWaitStep(flowId, head), Context.Caller));
        if (Context.Caller is { Actor: Actor.Person, Conversation: null } or { Actor: Actor.Kernel })
        {
            await Stop(flow, head, reason, ct);
            return;
        }
        // PersonCancelsApprovalWait above verified the flow is not waiting for a person; Flow.Cancel requires Person or Kernel.
        await StopElevated(flow, flowId, head, reason, ct);
    }

    private async Task StopElevated(IFlowStarter flow, string flowId, FlowHead head, string reason, CancellationToken ct = default)
    {
        if (services?.GetService<IKernel>() is { } kernel && NeuronId.TryParse(flowId, out var neuronId))
        {
            await kernel.CancelFlow(Id.Brain, neuronId, head.OperationId, reason, ct);
            return;
        }
        // Past the rule, the cancel is platform code: it runs as the kernel of this brain, never as an unstamped call.
        var edge = services?.GetService<EdgeAuthority>() ?? throw new InvalidOperationException("Stopping a flow needs the host's edge authority.");
        await edge.AsKernel(Id.Brain, async () => { await Stop(flow, head, reason, ct); return true; });
    }

    // The cancel itself, past the rule: the running step's leaf operation, then the flow.
    private async Task Stop(IFlowStarter flow, FlowHead head, string reason, CancellationToken ct = default)
    {
        if (head.OperationId is { Length: > 0 } id)
        {
            // An operation that already ended (or is unknown to the brain) has nothing to cancel.
            var operation = new OperationId(id);
            if (await operationsQuery.Read(operation, ct) is { Result: null }) { await operations.Cancel(operation, ct); }
        }
        await flow.Cancel(reason);
    }

    // The step at which the flow waits for a person, or null. A flow waits at its first step not done. The index learns the wait
    // from a signal, after the flow saved it: until it holds that step's wait, a waiting flow counts as waiting for a person.
    private string? PersonWaitStep(string flowId, FlowHead head)
    {
        if (head.Status != FlowStatus.Waiting) { return null; }
        var step = head.Steps.FirstOrDefault(name => !head.Done.Contains(name)) ?? "its current step";
        return State.Flows.GetValueOrDefault(flowId)?.Waiting is { Reason: not WaitReason.Approval } wait && wait.Step == step ? null : step;
    }

    public Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        return Flow(flowId).Relate(relation);
    }

    [On]
    public Task OnFlowSubject(FlowSubject named)
    {
        ArgumentNullException.ThrowIfNull(named);
        return Fold(named.FlowId, entry => entry.Subject == named.Subject ? entry : entry with { Subject = named.Subject });
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
                : entry with { Status = FlowStatus.Running, Current = step.Step, OperationId = null },
                readHead: true),
            StepOutcome.Completed => Fold(step.FlowId, entry => entry.Done.Contains(step.Step) ? entry : entry with
            {
                Done = [.. entry.Done, step.Step],
                Status = entry.Status == FlowStatus.Pending ? FlowStatus.Running : entry.Status,
                Current = entry.Current == step.Step ? null : entry.Current,
                OperationId = entry.Current == step.Step ? null : entry.OperationId,
            }),
            // A repeated step waits again: the wait it resolved before no longer answers for it, so its next wait is shown and
            // can be resumed. Its detail is taken as any other step's.
            StepOutcome.Repeated => Fold(step.FlowId, entry => entry.Finished
                || (entry.Detail == step.Detail && entry.ResolvedStep != step.Step && !entry.ResolvedSteps.Contains(step.Step, StringComparer.Ordinal)) ? entry : entry with
                {
                    Detail = step.Detail,
                    ResolvedStep = entry.ResolvedStep == step.Step ? null : entry.ResolvedStep,
                    ResolvedSteps = [.. entry.ResolvedSteps.Where(resolved => !string.Equals(resolved, step.Step, StringComparison.Ordinal))],
                }),
            _ => Fold(step.FlowId, entry => entry.Detail == step.Detail ? entry : entry with { Detail = step.Detail }),
        };
    }

    [On]
    public Task OnFlowWaiting(FlowWaiting waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        // A wait is applied only for the current step (or when none is current), and never for a step already done or whose
        // wait was already resolved; ResolvedStep stays, so a FlowWaiting redelivered after a later step started is ignored.
        return Fold(waiting.FlowId, entry => entry.Finished || entry.Done.Contains(waiting.Step) || entry.ResolvedStep == waiting.Step
            || entry.ResolvedSteps.Contains(waiting.Step, StringComparer.Ordinal)
            || (entry.Current is not null && entry.Current != waiting.Step) ? entry : entry with
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
        return Fold(resolve.FlowId, entry =>
        {
            var nextResolvedSteps = entry.ResolvedSteps.Contains(resolve.Step, StringComparer.Ordinal)
                ? entry.ResolvedSteps
                : [.. entry.ResolvedSteps, resolve.Step];
            return entry.Waiting is null
                ? (entry.ResolvedStep == resolve.Step && entry.ResolvedSteps.SequenceEqual(nextResolvedSteps) ? entry : entry with { ResolvedStep = resolve.Step, ResolvedSteps = nextResolvedSteps })
                : entry.Waiting.Step != resolve.Step ? entry : entry with
                {
                    ResolvedStep = resolve.Step,
                    ResolvedSteps = nextResolvedSteps,
                    Status = resolve.Outcome is WaitOutcome.Approved or WaitOutcome.Arrived && !entry.Finished ? FlowStatus.Running : entry.Status,
                    Waiting = null,
                    Current = null,
                };
        });
    }

    [On]
    public Task OnFlowDone(FlowDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        // An entry already finished from the flow's head (read while folding an earlier signal) still takes the outcome's detail.
        return Fold(done.FlowId, entry => entry.Finished ? entry with
        {
            Current = null,
            Waiting = null,
            OperationId = null,
            Ended = entry.Ended ?? Context.Time.GetUtcNow(),
            Detail = done.Detail ?? entry.Detail,
        } : entry with
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
    // read when readHead asks for it or the entry has none yet, unless skipHead is specified.
    private async Task Fold(string flowId, Func<FlowEntry, FlowEntry> change, bool readHead = false, bool readFinished = false, bool skipHead = false)
    {
        var known = State.Flows.TryGetValue(flowId, out var existing);
        var entry = known ? existing! : Placeholder(flowId);
        var next = change(entry);
        var wantsHead = !skipHead && (next.Finished ? readFinished && !next.HeadKnown : readHead || !next.HeadKnown);
        if (wantsHead && next.HeadAttempts < FlowEntry.MaxHeadAttempts) { next = await WithHead(next); }
        if (known && ReferenceEquals(next, existing)) { return; }
        await _saving.WaitAsync();
        try { await Save(State.With(next), new FlowsChanged(flowId)); }
        finally { _saving.Release(); }
    }

    private FlowEntry Placeholder(string flowId)
        => new(flowId, NeuronId.TryParse(flowId, out var id) ? id.Type : "", "", "", "", FlowStatus.Pending, [], [], null, null, [], null,
            Context.Time.GetUtcNow(), null, null);

    private async Task<FlowEntry> WithHead(FlowEntry entry)
    {
        try
        {
            var head = await Flow(entry.FlowId).Head();
            var merged = entry.Merge(head);
            return merged.Finished && merged.Ended is null
                ? merged with { Ended = Context.Time.GetUtcNow() }
                : merged;
        }
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
            && await operationsQuery.Read(new OperationId(operation), ct) is { Result: null } running)
        {
            fraction += Math.Clamp(running.Fraction, 0, 1) / steps;
        }
        return new FlowView(entry.FlowId, entry.Type, entry.Title, entry.Owner, entry.TraceId, StatusOf(entry, flows), entry.Steps, entry.Done,
            entry.Current, entry.Waiting?.Reason, entry.Waiting?.Step, entry.Waiting?.ConfirmationPlanId, Math.Min(1d, fraction), entry.Relations,
            [.. BlockersOf(entry, flows)], entry.ParentFlowId, entry.Started, entry.Ended, entry.Detail, entry.App, entry.Conversation, entry.Subject);
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

    private ILogger Logger() => (ILogger?)logger ?? OsConsoleLog.For<FlowsNeuron>();

    private Caller RequiredCaller()
        => Context.Caller ?? throw new UnauthorizedAccessException("A flow is started or resumed only by a stamped caller.");

    // Who the flow runs for, as its label: the person the caller acts for, or the actor itself for module, scheduler and kernel
    // work. Never a fallback identity: a call with no caller is refused (owner decision 3, 17 Nov).
    private string Owner()
    {
        var caller = RequiredCaller();
        return CallerRules.ActsFor(caller) ?? caller.Actor switch
        {
            Actor.Module module => module.Id,
            Actor.Scheduler => "scheduler",
            _ => "kernel",
        };
    }

    // The app a flow is recorded under: the calling app, or the assistant for an agent.
    private static string? StarterApp(Caller? caller)
        => CallerRules.IsApp(caller, out var app) ? app : caller?.Actor is Actor.Agent ? AssistantApp : null;
}

internal static partial class FlowsLog
{
    // Logged once, when retries stop: the index shows the flow without its steps, owner or progress.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The flows index could not read flow {FlowId} after {Attempts} attempts; it stops trying")]
    public static partial void HeadUnread(ILogger logger, Exception exception, string flowId, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The flows index could not read {What} for security interrupts; it keeps what it had")]
    public static partial void InterruptsUnread(ILogger logger, Exception exception, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The flows index could not cancel flow {FlowId} on a security interrupt; it goes on with the others")]
    public static partial void InterruptCancelFailed(ILogger logger, Exception exception, string flowId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The flows graph source could not list the flows of brain {Brain}")]
    public static partial void GraphUnlisted(ILogger logger, Exception exception, string brain);
}
