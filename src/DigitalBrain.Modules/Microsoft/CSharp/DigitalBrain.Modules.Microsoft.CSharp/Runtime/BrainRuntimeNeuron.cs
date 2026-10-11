using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Metadata;
using Orleans.Serialization.Invocation;
using Orleans.Timers;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// A brain's programs, their durable subscriptions and the one sandbox session that runs them. Every
// change goes through the same rules: a session is wanted while anything is assigned; the host is told
// the difference over its stream; signals buffer until the host acknowledges them. The reminder is the
// backstop that relaunches a lost session, not the detector. What the synapses' runs do becomes facts that
// the brain's runs neuron publishes, one hand-off per turn after the turn's write.
// Every script call and every live signal of the brain passes through here, so a turn holds only state
// changes: the pure reads interleave (their contracts say so), a live signal is a nudge noted without a
// turn, compiling happens before an Assign takes its turn, and pricing a run happens after the ack's.
[GrainType(GrainTypeName)]
[MayInterleave(nameof(IsNudge))]
internal sealed class BrainRuntimeNeuron(
    [PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<BrainRuntimeState> store,
    SessionRunner runner,
    SessionTokens tokens,
    SessionStreams streams,
    IBehaviorAssemblies assemblies,
    CSharpScriptCheck compiler,
    CompiledTypes types,
    IReminderRegistry reminders,
    TimeProvider time,
    ISignalJournal journal,
    IOptions<CSharpOptions> options,
    IOptions<CSharpDeploymentSettings> deployment,
    ILogger<BrainRuntimeNeuron> logger)
    : Neuron<BrainRuntimeState>(store), IBrainRuntime, IBrainRuntimeEdge, IBrainPrograms, IBrainProgramChanges, IBrainRuntimeArrivals, IBrainRuntimeSession, IRemindable, INeuronObserver
{
    internal const string GrainTypeName = "microsoft.csharp.runtime";
    internal const string ReconcileReminder = "reconcile";
    // Orleans reminders fire at most once a minute.
    private static readonly TimeSpan ReconcilePeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AssemblyUrlLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LogsCap = TimeSpan.FromSeconds(10);
    // Rotated well inside SessionTokens.Lifetime, so a reminder that fires late still beats expiry.
    private static readonly TimeSpan TokenRotation = TimeSpan.FromMinutes(40);
    // Development sandboxes reach the brain on the Docker host.
    internal const string DockerHost = "host.docker.internal";

    private readonly Dictionary<string, INeuron> _watched = new(StringComparer.Ordinal);
    // Last status the sandbox reported per behavior id. Memory only: a fresh activation shows Starting until the host speaks.
    private readonly Dictionary<string, (string Status, string? Error)> _reported = new(StringComparer.Ordinal);
    // The trace each pushed batch carried, per (subscription, watermark), so its ack prices the CPU it cost under that trace.
    private readonly Dictionary<(string Subscription, long Watermark), BatchTrace> _batchTraces = new();
    // Batches that reached a live stream and are not acknowledged yet, per (subscription, watermark): each started a run.
    private readonly HashSet<(string Subscription, long Watermark)> _started = [];
    // What each behavior's runs did since this activation. Memory only, like _reported.
    private readonly Dictionary<string, RunCounts> _runs = new(StringComparer.Ordinal);
    // This turn's run facts: handed to the brain's runs neuron in one call, once the turn's write succeeded.
    private readonly List<RunFact> _facts = [];
    private static readonly ActivitySource Telemetry = new("DigitalBrain.Microsoft.CSharp.Runtime");
    private ICostMeter? _meter;
    private IGrainTimer? _watchRenewal;
    // The tail of this activation's sandbox calls; each runs after the one before it, whatever its outcome.
    private Task _sandboxCalls = Task.CompletedTask;
    // The tail of this activation's pricing of acknowledged runs, priced one after another off the acks' turns.
    private Task _pricing = Task.CompletedTask;
    // Sources a live signal nudged about that no read of their log has covered since: a nudge whose arrival
    // expired in the queue or failed is read by the next reminder, not left until the source publishes again.
    private readonly HashSet<string> _nudged = new(StringComparer.Ordinal);
    // Programs compiled and stored while the runtime went on, each waiting for its turn, by ticket.
    private readonly Dictionary<long, CompiledProgram> _compiled = [];
    // Per owner, the ticket of its program's last change (an Assign applied, or a Retire): an Assign that started
    // before that change and finished compiling after it is not applied.
    private readonly Dictionary<string, long> _changed = new(StringComparer.Ordinal);
    private long _tickets;

    private string RuntimeId => this.GetPrimaryKeyString();
    private string Brain => RuntimeId[..RuntimeId.LastIndexOf('/')];

    // The host composer may not have wired a meter yet; a brain without one simply does not price CPU.
    private ICostMeter? Meter => _meter ??= ServiceProvider.GetService<ICostMeter>();

    // A subscription to any brain's runs neuron is a reaction to run facts, and its own runs publish none: a
    // reaction to its own brain's runs would feed itself, and two brains reacting to each other's would feed each other.
    private static bool IsRunFacts(string neuron) => GrainId.Parse(neuron).Type.ToString() == BrainRunsNeuron.GrainTypeName;

    // The traces still held for pushed batches; a settled batch lets go of its trace.
    internal int TracedBatches => _batchTraces.Count;

    // A live signal from a watched source is only a nudge, noted without waiting for a turn, so its publisher
    // never waits on this runtime. Orleans lets anything interleave with a request this predicate admits while
    // that request runs, so it admits only OnSignalAsync, which completes synchronously.
    public static bool IsNudge(IInvokable request) => request.GetInterfaceType() == typeof(INeuronObserver);

    public Task<BrainRuntimeSnapshot> Inspect(CancellationToken cancellationToken = default)
        => Task.FromResult(new BrainRuntimeSnapshot(Snapshot.Session?.Status ?? SessionStatus.None, Snapshot.Session?.Id,
            Snapshot.Session?.Fingerprint, Snapshot.Session?.LastHeartbeat,
            [.. Snapshot.Programs.SelectMany(program => View(program).Behaviors)]));

    public async Task Touch(CancellationToken cancellationToken = default)
    {
        if (Snapshot.Programs.Length > 0) { await EnsureSessionAsync(); }
    }

    public async Task Hold(CancellationToken cancellationToken = default)
    {
        var start = await PrepareSessionAsync(restart: false) ?? throw NotListening();
        await _sandboxCalls;
        await runner.Ensure(start, cancellationToken);
    }

    // Interleaved (its contract says so): compiling is seconds of CPU, off this runtime's scheduler, and storing
    // the assemblies is I/O; neither touches the runtime's state. The compiled program then takes a turn of its own.
    public async Task<ProgramView> Assign(string owner, Dictionary<string, string> sources, Dictionary<string, string> settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        var ticket = ++_tickets;
        var caller = CallerContextStamper.TryGet(out var stamped) ? stamped : null;
        var compiled = await Task.Run(() => compiler.Compile(sources));
        if (!compiled.Success)
        {
            throw new InvalidOperationException("The program does not compile: "
                + string.Join("; ", compiled.Errors.Select(error => $"{error.File} line {error.Line} {error.Id}: {error.Message}")));
        }
        foreach (var behavior in compiled.Assemblies.Values) { await assemblies.Put(behavior, CancellationToken.None); }
        BehaviorAssembly[] behaviors = [.. compiled.Assemblies.OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => new BehaviorAssembly(file.Key, file.Value.Hash))];
        _compiled[ticket] = new(owner, behaviors, new(settings, StringComparer.Ordinal), caller);
        // This request holds no turn, so the call to itself waits only for the turn in progress, never for this one.
        try { return await this.AsReference<IBrainProgramChanges>().Apply(ticket); }
        finally { _compiled.Remove(ticket); }
    }

    // A synapse that is gone, or whose assembly changed, is not the synapse its subscriptions were declared by:
    // they go with it, and a changed synapse's new assembly declares its own when it runs. A program that now
    // speaks for another principal is judged again by every source it watches.
    public async Task<ProgramView> Apply(long ticket)
    {
        if (!_compiled.TryGetValue(ticket, out var compiled))
        { throw new InvalidOperationException("No program this runtime compiled waits under that ticket."); }
        var owner = compiled.Owner;
        if (_changed.GetValueOrDefault(owner) > ticket)
        { throw new InvalidOperationException($"The program of {owner} changed again while this one compiled; it was not applied."); }
        _changed[owner] = ticket;
        var behaviors = compiled.Behaviors;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", behaviors.Select(b => b.Path + ":" + b.Hash)))));
        var previous = Snapshot.Programs.FirstOrDefault(p => p.Owner == owner);
        var program = new ProgramAssignment(owner, hash, compiled.Settings, compiled.Caller ?? previous?.Principal, behaviors);
        var replaced = previous?.Behaviors.Except(behaviors).Select(b => b.Id(owner)).ToArray() ?? [];
        var removed = replaced.Except(behaviors.Select(b => b.Id(owner)), StringComparer.Ordinal).ToArray();
        await Save(Snapshot with
        {
            Programs = [.. Snapshot.Programs.Where(p => p.Owner != owner), program],
            Subscriptions = [.. Snapshot.Subscriptions.Where(s => !replaced.Contains(s.Behavior, StringComparer.Ordinal))],
        }, Changed());
        foreach (var gone in replaced) { Forget(gone); }
        if (replaced.Length > 0 || (previous is not null && previous.Principal != program.Principal)) { await WatchSourcesAsync(); }
        if (Snapshot.Session is null) { await EnsureSessionAsync(); }
        else
        {
            foreach (var gone in removed) { streams.TryPush(Snapshot.Session.Id, RetireEvent.Kind, new RetireEvent(gone)); }
            foreach (var behavior in behaviors) { streams.TryPush(Snapshot.Session.Id, AssignmentEvent.Kind, await AssignmentFor(program, behavior)); }
        }
        return View(program);
    }

    private sealed record CompiledProgram(string Owner, BehaviorAssembly[] Behaviors, Dictionary<string, string> Settings, CallerContext? Caller);

    public async Task Retire(string owner)
    {
        // The owner's latest change: an Assign still compiling from before it is not applied after it.
        _changed[owner] = ++_tickets;
        if (Snapshot.Programs.FirstOrDefault(p => p.Owner == owner) is not { } program) { return; }
        var ids = program.Behaviors.Select(b => b.Id(owner)).ToArray();
        await Save(Snapshot with
        {
            Programs = [.. Snapshot.Programs.Where(p => p.Owner != owner)],
            Subscriptions = [.. Snapshot.Subscriptions.Where(s => !ids.Contains(s.Behavior, StringComparer.Ordinal))],
        }, Changed());
        foreach (var id in ids) { Forget(id); }
        await WatchSourcesAsync();
        if (Snapshot.Session is { } session) { foreach (var id in ids) { streams.TryPush(session.Id, RetireEvent.Kind, new RetireEvent(id)); } }
        if (Snapshot.Programs.Length == 0) { await EndSessionAsync(); }
    }

    // What this activation remembers of a behavior that is no longer assigned, and of its batches in flight.
    private void Forget(string behavior)
    {
        _reported.Remove(behavior);
        _runs.Remove(behavior);
        var live = Snapshot.Subscriptions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        _started.RemoveWhere(key => !live.Contains(key.Subscription));
        foreach (var key in _batchTraces.Keys.Where(key => !live.Contains(key.Subscription)).ToArray()) { _batchTraces.Remove(key); }
    }

    // The behavior id's owner is a prefix; the longest matching owner wins.
    private ProgramAssignment? Find(string behavior)
        => Snapshot.Programs.Where(p => p.Behaviors.Any(b => b.Id(p.Owner) == behavior)).OrderByDescending(p => p.Owner.Length).FirstOrDefault();

    // The program a behavior belongs to and its own assembly's hash; null for a behavior no longer assigned.
    private (string Owner, string Hash)? Synapse(string behavior)
        => Find(behavior) is { } program && program.Behaviors.FirstOrDefault(b => b.Id(program.Owner) == behavior) is { } assembly
            ? (program.Owner, assembly.Hash)
            : null;

    Task<ProgramView?> IBrainPrograms.Describe(string owner)
        => Task.FromResult(Snapshot.Programs.FirstOrDefault(p => p.Owner == owner) is { } program ? View(program) : null);

    public Task<BehaviorView?> Describe(string behavior)
    {
        var program = Find(behavior);
        return Task.FromResult(program is null ? null : View(program).Behaviors.FirstOrDefault(view => view.Id == behavior));
    }

    public Task<string> Logs(string behavior, int tail)
        => Snapshot.Session is { Status: SessionStatus.Connected } session
            ? streams.RequestLogsAsync(session.Id, behavior, Math.Clamp(tail, 1, 5000), LogsCap)
            : Task.FromResult("");

    public Task<SessionAuthorization> Authorize(string session)
        => Task.FromResult(new SessionAuthorization(Speaks(session), Brain));

    public Task<BehaviorIdentity?> Identity(string session, string behavior)
    {
        RequireSpeaking(session);
        return Task.FromResult(Find(behavior) is { } program ? new BehaviorIdentity(program.Principal, program.Owner) : null);
    }

    // A host built from other contracts must not load behaviors compiled against these: it is refused,
    // stopped, and the reason stays in the session record.
    public async Task Connected(string session, string fingerprint)
    {
        RequireSpeaking(session);
        if (!ContractsFingerprint.Covers(fingerprint, ContractsFingerprint.Of(types.Assemblies)))
        {
            logger.LogError("Sandbox session {Session} of brain {Brain} carries other contracts than this silo; refused. Rebuild the sandbox image from this build.", session, Brain);
            await Save(Snapshot with { Session = Snapshot.Session! with { Status = SessionStatus.Refused, Fingerprint = fingerprint } }, Changed());
            OffTurn(runner.Stop, Brain, "stop");
            return;
        }
        await Save(Snapshot with { Session = Snapshot.Session! with { Status = SessionStatus.Connected, Fingerprint = fingerprint, LastHeartbeat = time.GetUtcNow() } }, Changed());
    }

    public Task Heartbeat(string session)
    {
        RequireSpeaking(session);
        return Save(Snapshot with { Session = Snapshot.Session! with { LastHeartbeat = time.GetUtcNow() } }, Changed());
    }

    public async Task<string> Subscribed(string session, string behavior, string neuron, string signal)
    {
        RequireSpeaking(session);
        if (Find(behavior) is null) { throw new ArgumentException($"{behavior} is not assigned to this brain.", nameof(behavior)); }
        if (!GrainId.TryParse(neuron, out _)) { throw new ArgumentException($"'{neuron}' is not a neuron id such as time.timer/tea.", nameof(neuron)); }
        // Watching itself would deadlock this non-reentrant grain and self-feed on every save.
        if (neuron == this.GetGrainId().ToString()) { throw new ArgumentException("A behavior cannot subscribe to its brain runtime.", nameof(neuron)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        _facts.Clear();
        var existing = Snapshot.Subscriptions.FirstOrDefault(s => s.Behavior == behavior && s.Neuron == neuron && s.Signal == signal);
        if (existing is null)
        {
            // A new subscription starts at its source's current seq; what came before is the retained seed's to give.
            var position = await journal.SignalsOf(GrainId.Parse(neuron), long.MaxValue);
            existing = new(Guid.NewGuid().ToString("N"), behavior, neuron, signal, [], 0, 0, position?.Last ?? 0);
            // A source that retains the signal seeds the new subscription with the backlog the synapse
            // missed. Watch before seeding: anything published between the seed and the watch arrives
            // through the watch itself, and a duplicate is at-least-once's to bear; nothing is lost.
            await Save(Snapshot with { Subscriptions = [.. Snapshot.Subscriptions, existing] }, Changed());
            try { await WatchSourcesAsync(required: existing); }
            catch (Exception error) when (IsRefusal(error)) { await RefuseAsync(existing); throw; }
            var seeded = await SeedRetainedAsync(neuron, signal, existing.Watermark);
            if (seeded.Length > 0)
            {
                var current = Snapshot.Subscriptions.First(s => s.Id == existing.Id);
                var known = seeded.ToHashSet(StringComparer.Ordinal);
                existing = current with { Pending = [.. seeded, .. current.Pending.Where(json => !known.Contains(json))] };
                await Save(Snapshot with { Subscriptions = [.. Snapshot.Subscriptions.Where(s => s.Id != existing.Id), existing] }, Changed());
            }
        }
        else
        {
            // Persistence can succeed before source registration fails. A retry is ready only
            // after the source has accepted the current selection, including the saved addition.
            try { await WatchAsync(neuron, GrainFactory.GetGrain<INeuron>(GrainId.Parse(neuron)), this.AsReference<INeuronObserver>(), required: existing); }
            catch (Exception error) when (IsRefusal(error)) { await RefuseAsync(existing); throw; }
            // The log after the watermark comes first; a source whose part is leased elsewhere fails the subscribe here.
            await CatchUpAsync(neuron, push: false);
            existing = Snapshot.Subscriptions.First(s => s.Id == existing.Id);
            // A reloaded behavior gets what waited while it was down without another arrival.
            if (existing.Pending.Length > 0 && Snapshot.Session is { Status: SessionStatus.Connected }) { Push(session, existing); }
        }
        logger.LogInformation("BehaviorSubscribed {Behavior} {Neuron} {Signal}", behavior, neuron, signal);
        Flush();
        return existing.Id;
    }

    public Task Acknowledge(string session, string subscription, long watermark)
    {
        RequireSpeaking(session);
        return AcknowledgeAsync([new AcknowledgeReport(subscription, watermark)]);
    }

    public Task AcknowledgeMany(string session, AcknowledgeReport[] acknowledgements)
    {
        RequireSpeaking(session);
        ArgumentNullException.ThrowIfNull(acknowledgements);
        if (acknowledgements.Length is 0 or > RuntimeProtocol.MaximumAcknowledgements
            || acknowledgements.Any(ack => ack is null || string.IsNullOrWhiteSpace(ack.Subscription) || ack.Watermark < 0
                || ack.CpuMicroseconds < 0 || ack.WallMicroseconds < 0 || ack.Batches < 0))
        { throw new ArgumentException("An acknowledgement batch needs 1 to 256 valid subscription watermarks.", nameof(acknowledgements)); }
        return AcknowledgeAsync([.. acknowledgements.GroupBy(ack => ack.Subscription, StringComparer.Ordinal)
            .Select(group => new AcknowledgeReport(group.Key, group.Max(ack => ack.Watermark), group.Sum(ack => ack.CpuMicroseconds),
                group.Sum(ack => ack.WallMicroseconds), group.Sum(ack => ack.Batches), group.All(ack => ack.Skipped)))]);
    }

    // One write trims every acknowledged subscription. A report that moved Delivered is a finished run; a
    // replayed one is nothing. Either way the batches it covers, at or below its watermark, are no longer in flight.
    private async Task AcknowledgeAsync(AcknowledgeReport[] reports)
    {
        _facts.Clear();
        var byId = reports.ToDictionary(report => report.Subscription, StringComparer.Ordinal);
        var advanced = new List<(Subscription Before, Subscription After, AcknowledgeReport Report)>();
        var next = Snapshot.Subscriptions.Select(subscription =>
        {
            if (!byId.TryGetValue(subscription.Id, out var report)) { return subscription; }
            var trimmed = subscription.Acknowledge(report.Watermark);
            if (trimmed.Delivered != subscription.Delivered) { advanced.Add((subscription, trimmed, report)); }
            return trimmed;
        }).ToArray();
        if (advanced.Count > 0) { await Save(Snapshot with { Subscriptions = next }, Changed()); }
        var traces = reports.ToDictionary(report => report.Subscription, report => Settle(report.Subscription, report.Watermark), StringComparer.Ordinal);
        foreach (var (before, after, report) in advanced)
        {
            if (IsRunFacts(before.Neuron) || Synapse(before.Behavior) is not { } synapse) { continue; }
            Ran(new BehaviorRan(RuntimeId, synapse.Owner, before.Behavior, synapse.Hash, before.Neuron, before.Signal, after.Delivered,
                report.Skipped ? RunOutcome.Skipped : RunOutcome.Succeeded, report.Batches, report.WallMicroseconds, report.CpuMicroseconds, null), traces[report.Subscription]);
        }
        Flush();
        // A replayed ack prices nothing again. Metering observes the run: it is priced after this turn, so a slow
        // ledger never holds the runtime, and a meter that fails does not fail the ack.
        PricedRun[] priced = [.. advanced.Where(run => run.Report.CpuMicroseconds > 0).Select(run => new PricedRun(run.Report.Subscription,
            run.Report.Watermark, run.Report.CpuMicroseconds, run.Before.Behavior, Find(run.Before.Behavior)?.Owner, run.After.Delivered, traces[run.Report.Subscription]?.Trace))];
        if (priced.Length > 0 && Meter is { } meter) { _pricing = PriceAsync(_pricing, meter, priced); }
    }

    private sealed record PricedRun(string Subscription, long Watermark, long CpuMicroseconds, string Behavior, string? App, long Delivered, Trace? Trace);

    // A pushed batch's trace and the traceparent it carried: the run's span sits under the batch's own span.
    private sealed record BatchTrace(Trace Trace, string TraceParent);

    // Runs on this activation's scheduler between turns and reads nothing of the runtime's state.
    private async Task PriceAsync(Task previous, ICostMeter meter, PricedRun[] runs)
    {
        await previous;
        foreach (var run in runs)
        {
            using var entered = run.Trace is { } batch ? TraceContext.Enter(batch) : null;
            try
            {
                await meter.RecordAsync(CostKind.Script, "cpu-ms", run.CpuMicroseconds / 1000m, "ms", Funding.Local,
                    $"{run.Subscription}:{run.Watermark}", CancellationToken.None, new CostSubject(Synapse: run.Behavior, App: run.App));
            }
            catch (Exception error)
            { logger.LogWarning(error, "Brain {Brain} could not price {Behavior}'s run through {Watermark}.", Brain, run.Behavior, run.Delivered); }
        }
    }

    // The batches of a subscription at or below an acknowledged watermark are settled: none is in flight any
    // more. The trace of the latest of them is the run's.
    private BatchTrace? Settle(string subscription, long watermark)
    {
        BatchTrace? latest = null;
        var at = long.MinValue;
        foreach (var key in _batchTraces.Keys.Where(key => key.Subscription == subscription && key.Watermark <= watermark).ToArray())
        {
            if (key.Watermark > at) { (at, latest) = (key.Watermark, _batchTraces[key]); }
            _batchTraces.Remove(key);
        }
        _started.RemoveWhere(key => key.Subscription == subscription && key.Watermark <= watermark);
        return latest;
    }

    // The status the sandbox last reported for an assigned behavior. An exit is remembered, never disarmed:
    // the sandbox restarts an exited behavior itself while its assignment stands.
    public Task Reported(string session, BehaviorStatusReport status)
    {
        RequireSpeaking(session);
        ArgumentNullException.ThrowIfNull(status);
        _facts.Clear();
        if (Find(status.Behavior) is { } program && program.Behaviors.FirstOrDefault(b => b.Id(program.Owner) == status.Behavior && b.Hash == status.Hash) is { } assembly)
        {
            _reported[status.Behavior] = (status.Status, status.Error);
            if (status is { Status: "Exited", ExitCode: not (null or 0) }) { Failed(program.Owner, assembly.Hash, status); }
        }
        Flush();
        return Task.CompletedTask;
    }

    // A behavior that exited while batches were in flight failed those runs. They are no longer started, so
    // the batch resent to the restarted behavior starts a run again.
    private void Failed(string owner, string hash, BehaviorStatusReport status)
    {
        foreach (var subscription in Snapshot.Subscriptions.Where(s => s.Behavior == status.Behavior && !IsRunFacts(s.Neuron)))
        {
            var inFlight = _started.Where(key => key.Subscription == subscription.Id).ToArray();
            if (inFlight.Length == 0) { continue; }
            var watermark = inFlight.Max(key => key.Watermark);
            _started.ExceptWith(inFlight);
            Ran(new BehaviorRan(RuntimeId, owner, status.Behavior, hash, subscription.Neuron, subscription.Signal, watermark,
                RunOutcome.Failed, inFlight.Length, 0, 0, status.Error), _batchTraces.GetValueOrDefault((subscription.Id, watermark)));
        }
    }

    // The pull path: the edge streams every batch returned here, so each starts its run.
    public Task<IReadOnlyList<SignalBatchEvent>> PendingBatches(string session)
    {
        RequireSpeaking(session);
        _facts.Clear();
        var pending = Snapshot.Subscriptions.Where(s => s.Pending.Length > 0).ToArray();
        IReadOnlyList<SignalBatchEvent> batches = [.. pending.Select(Batch)];
        foreach (var subscription in pending) { Started(subscription); }
        Flush();
        return Task.FromResult(batches);
    }

    public async Task<IReadOnlyList<AssignmentEvent>> Assignments(string session)
    {
        RequireSpeaking(session);
        var assigned = new List<AssignmentEvent>();
        foreach (var program in Snapshot.Programs)
        { foreach (var behavior in program.Behaviors) { assigned.Add(await AssignmentFor(program, behavior)); } }
        return assigned;
    }

    // Interleaved (IsNudge): notes the source and leaves the reading to an arrival in a turn of its own.
    Task INeuronObserver.OnSignalAsync(Signal signal)
    {
        var source = signal.Publisher.Name;
        if (!Snapshot.Subscriptions.Any(s => s.Signal == signal.GetType().Name && s.Neuron == source)) { return Task.CompletedTask; }
        _nudged.Add(source);
        return this.AsReference<IBrainRuntimeArrivals>().Arrived(signal);
    }

    public async Task Arrived(Signal signal)
    {
        _facts.Clear();
        var source = signal.Publisher.Name;
        // Read now: a nudge that comes while the read is in flight marks the source again, a failed read keeps it marked.
        _nudged.Remove(source);
        bool journaled;
        try { journaled = await CatchUpAsync(source); }
        catch { _nudged.Add(source); throw; }
        if (journaled) { Flush(); return; }
        var json = JsonSerializer.Serialize(signal, signal.GetType(), RuntimeProtocol.Json);
        var name = signal.GetType().Name;
        var cap = options.Value.MaximumPending;
        var arrived = new List<Subscription>();
        var drops = new List<(Subscription Buffered, long Count)>();
        var next = Snapshot.Subscriptions.Select(s =>
        {
            if (s.Signal != name || s.Neuron != signal.Publisher.Name || !Accepted(s)) { return s; }
            var buffered = s.Arrive(json, cap);
            arrived.Add(buffered);
            if (buffered.Dropped > s.Dropped) { drops.Add((buffered, buffered.Dropped - s.Dropped)); }
            return buffered;
        }).ToArray();
        if (arrived.Count == 0) { return; }
        await Save(Snapshot with { Subscriptions = next }, Changed());
        foreach (var (buffered, count) in drops) { Dropped(buffered, count); }
        if (Snapshot.Session is { Status: SessionStatus.Connected } session)
        {
            foreach (var subscription in arrived) { Push(session.Id, subscription); }
        }
        Flush();
    }

    // A reactivated runtime has no source's acceptance yet: each source is watched on its lane, off every turn,
    // and only then is its log read, in a short turn of its own.
    public Task CatchUp()
    {
        foreach (var neuron in Snapshot.Subscriptions.Select(s => s.Neuron).Distinct(StringComparer.Ordinal).ToArray()) { _ = ReadAfterWatchAsync(neuron); }
        return Task.CompletedTask;
    }

    private async Task ReadAfterWatchAsync(string neuron)
    {
        await WatchOffTurn(neuron, again: false);
        try { await this.AsReference<IBrainRuntimeArrivals>().Read(neuron); }
        catch (Exception error) { logger.LogDebug(error, "Brain {Brain} could not ask itself to read {Neuron}'s part log.", Brain, neuron); }
    }

    public Task Read(string source) => CatchUpSourcesAsync([source]);

    // A source whose log cannot be read now stays nudged, so the next reminder reads it again.
    private async Task CatchUpSourcesAsync(string[] sources)
    {
        _facts.Clear();
        foreach (var neuron in sources)
        {
            _nudged.Remove(neuron);
            try { await CatchUpAsync(neuron); }
            catch (Exception error)
            {
                _nudged.Add(neuron);
                logger.LogWarning(error, "Brain {Brain} could not read {Neuron}'s part log from its watermark; its next reminder retries.", Brain, neuron);
            }
        }
        Flush();
    }

    // On a host with a part log a live signal is a nudge: the source's log after each watermark is what is buffered,
    // so a signal the runtime never heard live arrives too and none arrives twice. False when nothing is journaled.
    // Only a subscription its source accepted on this activation is buffered; any other keeps its watermark.
    private async Task<bool> CatchUpAsync(string neuron, bool push = true)
    {
        var matching = Snapshot.Subscriptions.Where(s => s.Neuron == neuron && Accepted(s)).ToArray();
        if (matching.Length == 0) { return true; }
        var since = matching.Min(s => s.SourceSeq);
        if (await journal.SignalsOf(GrainId.Parse(neuron), since) is not { } read) { return false; }
        var cap = options.Value.MaximumPending;
        var arrived = new List<Subscription>();
        var drops = new List<(Subscription Buffered, long Count)>();
        var changed = false;
        var next = Snapshot.Subscriptions.Select(s =>
        {
            if (s.Neuron != neuron || !Accepted(s)) { return s; }
            if (read.Floor > s.SourceSeq)
            { logger.LogWarning("SubscriptionGap {Subscription} {Neuron}: its watermark {Watermark} is below seq {Floor}, the oldest its source's log still serves.", s.Id, neuron, s.SourceSeq, read.Floor); }
            var buffered = s;
            foreach (var item in read.Signals.Where(j => j.Sequence > s.SourceSeq && j.Signal.GetType().Name == s.Signal))
            { buffered = buffered.Arrive(JsonSerializer.Serialize(item.Signal, item.Signal.GetType(), RuntimeProtocol.Json), cap); }
            var through = Math.Max(Math.Max(s.SourceSeq, read.Floor), read.Signals.Length == 0 ? 0 : read.Signals[^1].Sequence);
            buffered = buffered with { SourceSeq = through };
            if (buffered.Pending.Length != s.Pending.Length || buffered.Delivered != s.Delivered) { arrived.Add(buffered); }
            if (buffered.Dropped > s.Dropped) { drops.Add((buffered, buffered.Dropped - s.Dropped)); }
            changed |= buffered != s;
            return buffered;
        }).ToArray();
        if (!changed) { return true; }
        await Save(Snapshot with { Subscriptions = next }, Changed());
        foreach (var (buffered, count) in drops) { Dropped(buffered, count); }
        if (push && Snapshot.Session is { Status: SessionStatus.Connected } session)
        {
            foreach (var subscription in arrived) { Push(session.Id, subscription); }
        }
        return true;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReconcileReminder) { return; }
        if (Snapshot.Programs.Length == 0) { await EndSessionAsync(); return; }
        if (_watched.Count == 0 && Snapshot.Subscriptions.Length > 0) { await WatchSourcesAsync(); }
        if (_nudged.Count > 0) { await CatchUpSourcesAsync([.. _nudged]); }
        var before = Snapshot.Session?.Id;
        await EnsureSessionAsync();
        if (Snapshot.Session is not { } session || session.Id != before) { return; }
        // Rotated only once the host has it; a rotation its stream missed is retried on the next reminder.
        if (session.TokenIssued < time.GetUtcNow() - TokenRotation
            && streams.TryPush(session.Id, TokenEvent.Kind, new TokenEvent(tokens.Issue(Brain, session.Id))))
        { await Save(Snapshot with { Session = session with { TokenIssued = time.GetUtcNow() } }, Changed()); }
    }

    // A source's own signal may be what activated this runtime, so waiting for its watch now would deadlock;
    // the catch-up watches every source on its lane, off the activation turn, before it reads the source's log.
    internal Task ResumeAsync()
    {
        if (Snapshot.Subscriptions.Length > 0)
        {
            SyncWatched();
            // Off the activation turn: a source's log may be on a part this machine has yet to open.
            _ = this.AsReference<IBrainRuntimeArrivals>().CatchUp();
        }
        // Signals may have arrived while watches were absent. Relaunch disposable behaviors so
        // their subscribe-then-read recovery runs against durable state, with fresh credentials.
        return Snapshot.Programs.Length > 0 ? EnsureSessionAsync(restart: true) : Task.CompletedTask;
    }

    // A new session id per launch: the previous session's token stops speaking for this brain.
    // One rule for assignment, sign-in and the reminder. A missing, refused or silent session is replaced by
    // a new one (the old token stops speaking); a live one is ensured again, refreshing its token or
    // restoring it if the host lost it. Heartbeats are the detector; nothing here probes the host.
    // A brain created while its silo is still starting has no address a sandbox can dial yet: the program
    // is already assigned, and the reminder registered with the session starts it once the silo listens.
    // A silo that listens, but on no http:// address, with no EdgeUrl configured, never will: that is a
    // misconfiguration, reported on every reminder, and the assignment still stands.
    private async Task EnsureSessionAsync(bool restart = false)
    {
        if (await PrepareSessionAsync(restart) is { } start) { OffTurn(runner.Ensure, start, "start"); }
        else if (ServerAddresses() is { Count: > 0 } addresses)
        { logger.LogError("Brain {Brain}'s sandbox session cannot start on {Addresses}: {Reason}", Brain, string.Join(", ", addresses), NotListening().Message); }
        else { logger.LogInformation("Brain {Brain}'s sandbox session waits until this silo listens; its reminder starts it.", Brain); }
    }

    private async Task<SessionStart?> PrepareSessionAsync(bool restart)
    {
        var session = Snapshot.Session;
        if (restart || session is null || session.Status == SessionStatus.Refused || Silent(session))
        {
            session = new(Guid.NewGuid().ToString("N"), SessionStatus.Starting, null, null, time.GetUtcNow(), time.GetUtcNow());
            await Save(Snapshot with { Session = session }, Changed());
            await reminders.RegisterOrUpdateReminder(this.GetGrainId(), ReconcileReminder, ReconcilePeriod, ReconcilePeriod);
        }
        return EdgeUrl() is { } edge ? new SessionStart(Brain, session.Id, tokens.Issue(Brain, session.Id), edge) : null;
    }

    private bool Silent(SessionRecord session) => (session.LastHeartbeat ?? session.Started) < time.GetUtcNow() - SilenceLimit;

    // The sandbox is called off this runtime's turn: a host that is down, slow or behind a proxy that never
    // answers must not hold the brain's runtime. The calls still reach the host in the order they were made,
    // so a stop never lands after the start that follows it. A failure is the reminder's to retry.
    private void OffTurn<T>(Func<T, CancellationToken, Task> call, T argument, string what)
    {
        var brain = Brain;
        var previous = _sandboxCalls;
        _sandboxCalls = Task.Run(async () =>
        {
            await previous;
            try { await call(argument, CancellationToken.None); }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            { logger.LogWarning(error, "The sandbox did not {What} brain {Brain}'s session; its reminder retries.", what, brain); }
            catch (Exception error)
            { logger.LogError(error, "Could not {What} brain {Brain}'s sandbox session; its reminder retries.", what, brain); }
        });
    }

    private async Task EndSessionAsync()
    {
        if (await reminders.GetReminder(this.GetGrainId(), ReconcileReminder) is { } reminder)
        { await reminders.UnregisterReminder(this.GetGrainId(), reminder); }
        if (Snapshot.Session is null) { return; }
        OffTurn(runner.Stop, Brain, "stop");
        await Save(Snapshot with { Session = null }, Changed());
    }

    private bool Speaks(string session)
        => Snapshot.Session is { Status: not SessionStatus.Refused } current && session.Length > 0 && session == current.Id;

    private void RequireSpeaking(string session)
    {
        if (!Speaks(session)) { throw new UnauthorizedAccessException("This session no longer speaks for its brain."); }
    }

    private async Task<AssignmentEvent> AssignmentFor(ProgramAssignment program, BehaviorAssembly behavior)
        => new(behavior.Id(program.Owner), behavior.Hash,
            Reachable(await assemblies.ReadUrl(behavior.Hash, AssemblyUrlLifetime, CancellationToken.None)),
            Reachable(await assemblies.ReadUrl(behavior.Hash + ".pdb", AssemblyUrlLifetime, CancellationToken.None)),
            program.Settings);

    // Local blob storage answers on this machine's loopback, which no sandbox can reach; the Docker host
    // can. A SAS signs the path and query, not the host, so the rewritten URL stays valid.
    internal static Uri Reachable(Uri url) => url.IsLoopback ? new UriBuilder(url) { Host = DockerHost }.Uri : url;

    // The batch carries the trace of its first signal, so the behavior's reaction joins it; that same
    // trace is remembered until the ack, so the ack prices the CPU the run cost under it.
    private SignalBatchEvent Batch(Subscription subscription)
    {
        var trace = FirstSignalTrace(subscription.Pending);
        var traceParent = trace is null ? System.Diagnostics.Activity.Current?.Id : $"00-{trace.TraceId}-{System.Diagnostics.ActivitySpanId.CreateRandom().ToHexString()}-01";
        if (trace is not null) { _batchTraces[(subscription.Id, subscription.Watermark)] = new(trace, traceParent!); }
        return new SignalBatchEvent(subscription.Id, subscription.Pending, subscription.Watermark, traceParent);
    }

    private static Trace? FirstSignalTrace(string[] pending)
        => pending.Length == 0 ? null : JsonSerializer.Deserialize<Signal>(pending[0], RuntimeProtocol.Json)?.Trace;

    private void Push(string session, Subscription subscription)
    {
        if (streams.TryPush(session, SignalBatchEvent.Kind, Batch(subscription))) { Started(subscription); }
    }

    // A batch on a live stream starts a run once per watermark: the same batch pushed again starts nothing.
    private void Started(Subscription subscription)
    {
        if (IsRunFacts(subscription.Neuron) || Synapse(subscription.Behavior) is not { } synapse
            || !_started.Add((subscription.Id, subscription.Watermark))) { return; }
        _facts.Add(new(new BehaviorStarted(RuntimeId, synapse.Owner, subscription.Behavior, synapse.Hash, subscription.Neuron,
            subscription.Signal, subscription.Watermark), _batchTraces.GetValueOrDefault((subscription.Id, subscription.Watermark))?.Trace ?? TraceContext.Current));
    }

    // Signals pushed out of a full buffer never reach the synapse: a run that cannot happen, counted, never silent.
    private void Dropped(Subscription buffered, long count)
    {
        if (IsRunFacts(buffered.Neuron) || Synapse(buffered.Behavior) is not { } synapse) { return; }
        Ran(new BehaviorRan(RuntimeId, synapse.Owner, buffered.Behavior, synapse.Hash, buffered.Neuron, buffered.Signal, buffered.Delivered,
            RunOutcome.Dropped, (int)Math.Min(count, int.MaxValue), 0, 0, null), null);
    }

    private void Ran(BehaviorRan ran, BatchTrace? batch)
    {
        var trace = batch?.Trace ?? TraceContext.Current;
        _facts.Add(new(ran, trace));
        var counts = _runs.GetValueOrDefault(ran.Behavior);
        _runs[ran.Behavior] = ran.Outcome switch
        {
            RunOutcome.Succeeded => counts with { Runs = counts.Runs + 1, LastDurationMicroseconds = ran.DurationMicroseconds },
            RunOutcome.Failed => counts with { Runs = counts.Runs + 1, Failures = counts.Failures + 1 },
            _ => counts,
        };
        logger.LogInformation("BehaviorRan {Behavior} {Outcome} {DurationMicroseconds}", ran.Behavior, ran.Outcome, ran.DurationMicroseconds);
        Span(ran, batch);
    }

    // The run as a span under the batch's own span, in the batch's trace, ending now and as long as the synapse
    // took. A parent without a span id would make .NET take the current request's span as parent, from another trace.
    private void Span(BehaviorRan ran, BatchTrace? batch)
    {
        var end = time.GetUtcNow();
        var parent = batch is not null && ActivityContext.TryParse(batch.TraceParent, null, out var context) ? context : default;
        using var activity = Telemetry.StartActivity("runtime.batch", ActivityKind.Internal, parent,
            startTime: end - TimeSpan.FromMicroseconds(ran.DurationMicroseconds));
        if (activity is null) { return; }
        activity.SetTag("digitalbrain.behavior", ran.Behavior).SetTag("digitalbrain.owner", ran.Owner)
            .SetTag("digitalbrain.source", ran.Source).SetTag("digitalbrain.signal", ran.SignalType)
            .SetTag("digitalbrain.outcome", ran.Outcome.ToString()).SetTag("digitalbrain.batches", ran.Batches)
            .SetTag("digitalbrain.watermark", ran.Watermark).SetTag("digitalbrain.cpu_us", ran.CpuMicroseconds);
        if (ran.Outcome == RunOutcome.Failed) { activity.SetStatus(ActivityStatusCode.Error, ran.Error); }
        activity.SetEndTime(end.UtcDateTime);
    }

    // One one-way call carries the turn's facts. Unstamped: the runtime is their author, whoever's call
    // produced them; the caller is restored for whatever the turn still does.
    private void Flush()
    {
        if (_facts.Count == 0) { return; }
        RunFact[] facts = [.. _facts];
        _facts.Clear();
        var caller = RequestContext.Get(CallerContextStamper.RequestContextKey);
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try { _ = GrainFactory.GetGrain<IBrainRunsRecorder>(BrainRunsKeys.For(Brain)).Record(facts); }
        finally { if (caller is not null) { RequestContext.Set(CallerContextStamper.RequestContextKey, caller); } }
    }

    private readonly record struct RunCounts(long Runs, long Failures, long LastDurationMicroseconds);

    // A source that retains the signal seeds a new subscription with the backlog the synapse missed. Any other
    // source, a runs neuron among them, contributes nothing and is not asked.
    private async Task<string[]> SeedRetainedAsync(string neuron, string signal, long since)
    {
        var source = GrainId.Parse(neuron);
        if (!Retains(source)) { return []; }
        var retained = await GrainFactory.GetGrain<IRetainsSignals>(source).Retained(signal, since);
        return [.. retained.Select(item => JsonSerializer.Serialize(item.Signal, item.Signal.GetType(), RuntimeProtocol.Json))];
    }

    // Whether the source's grain type implements IRetainsSignals is the cluster manifest's to say; asking a
    // source that does not would cross the cluster only to fail a cast.
    private bool Retains(GrainId source)
    {
        var retains = ServiceProvider.GetRequiredService<GrainInterfaceTypeResolver>().GetGrainInterfaceType(typeof(IRetainsSignals)).ToString();
        return ServiceProvider.GetRequiredService<GrainPropertiesResolver>().TryGetGrainProperties(source.Type, out var grain)
            && grain.Properties.Any(property => property.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix, StringComparison.Ordinal)
                && property.Value == retains);
    }

    private ProgramView View(ProgramAssignment program)
        => new(program.Owner, program.Hash, [.. program.Behaviors.Select(behavior =>
        {
            var id = behavior.Id(program.Owner);
            (string Status, string? Error) reported = _reported.TryGetValue(id, out var last) ? last : ("Starting", null);
            var runs = _runs.GetValueOrDefault(id);
            return new BehaviorView(id, behavior.Hash, reported.Status, reported.Error,
                [.. Snapshot.Subscriptions.Where(s => s.Behavior == id).Select(s => new CSharpSubscriptionView(s.Neuron, s.Signal, s.Pending.Length, s.Delivered, s.Dropped))],
                runs.Runs, runs.Failures, runs.LastDurationMicroseconds);
        })]);

    private BrainRuntimeChanged Changed() => new(RuntimeId);

    // The host dials the brain's own edge: configured, or this silo's HTTP address on the Docker host.
    private string? EdgeUrl()
    {
        var root = deployment.Value.EdgeUrl is { Length: > 0 } configured ? configured : ListeningUrl();
        return root is null ? null : root.TrimEnd('/') + "/brains/" + Uri.EscapeDataString(Brain) + "/";
    }

    private static InvalidOperationException NotListening()
        => new($"Configure {CSharpDeploymentSettings.SectionName}:EdgeUrl; this brain listens on no HTTP address a sandbox can reach.");

    // Empty while the silo has yet to bind its listeners.
    private ICollection<string>? ServerAddresses() => ServiceProvider.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;

    private string? ListeningUrl()
    {
        var listening = ServerAddresses()?.FirstOrDefault(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (listening is null) { return null; }
        var wildcard = listening.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal)
            .Replace("[::]", "localhost", StringComparison.Ordinal).Replace("0.0.0.0", "localhost", StringComparison.Ordinal);
        return new UriBuilder(wildcard) { Host = DockerHost }.Uri.ToString();
    }

    // Every source is watched off the turn, on its own lane (WatchOffTurn); a turn waits only for the source of
    // the subscription it is making, whose verdict it answers.
    private async Task WatchSourcesAsync(Subscription? required = null)
    {
        foreach (var gone in SyncWatched()) { _ = WatchOffTurn(gone); }
        foreach (var key in _watched.Keys.Where(key => key != required?.Neuron).ToArray()) { _ = WatchOffTurn(key); }
        if (required is not null) { await WatchAsync(required.Neuron, _watched[required.Neuron], this.AsReference<INeuronObserver>(), required); }
    }

    // A source's lane: one watch of a source at a time, so a source that hangs holds only its own lane until its call
    // times out, and at most ConcurrentWatches sources are asked at once. Nothing awaits a lane inside a turn. A lane
    // asked again while it runs runs once more, against the state of then; a renewal only joins it.
    private Task WatchOffTurn(string key, bool again = true)
    {
        if (_lanes.TryGetValue(key, out var lane))
        {
            if (again) { _rewatch.Add(key); }
            return lane;
        }
        lane = LaneAsync(key);
        if (!lane.IsCompleted) { _lanes[key] = lane; }
        return lane;
    }

    private async Task LaneAsync(string key)
    {
        try
        {
            do
            {
                _rewatch.Remove(key);
                await _watchSlots.WaitAsync();
                try { await ReconcileAsync(key); }
                finally { _watchSlots.Release(); }
            }
            while (_rewatch.Contains(key));
        }
        finally { _lanes.Remove(key); }
    }

    // A source some subscription names is watched as each of its programs. One no subscription names any more is
    // unwatched as a program it accepted, as it was watched; one no program watched on this activation as no
    // program, as grain code is. A source that is gone (its type left the host) or refuses the unwatch keeps the
    // observer only until its lease lapses.
    private async Task ReconcileAsync(string key)
    {
        var observer = this.AsReference<INeuronObserver>();
        try
        {
            if (_watched.TryGetValue(key, out var watched)) { await WatchAsync(key, watched, observer, required: null); return; }
            var verdicts = _accepted.Where(verdict => verdict.Key.Source == key).ToArray();
            foreach (var verdict in verdicts) { _accepted.Remove(verdict.Key); }
            _unwatchable.RemoveWhere(refused => refused.Source == key);
            await AsProgram(verdicts.Length > 0 ? verdicts[0].Value : null, () => GrainFactory.GetGrain<INeuron>(GrainId.Parse(key)).Unwatch(observer));
        }
        catch (Exception error)
        { logger.LogDebug(error, "Brain runtime {Runtime} could not watch or unwatch {Source}.", RuntimeId, key); }
    }

    private const int ConcurrentWatches = 8;
    private readonly SemaphoreSlim _watchSlots = new(ConcurrentWatches);
    private readonly Dictionary<string, Task> _lanes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rewatch = new(StringComparer.Ordinal);

    // A source that refuses the program refuses the subscription: none is kept for a later turn to feed, and a
    // source no other subscription names is let go.
    private async Task RefuseAsync(Subscription refused)
    {
        await Save(Snapshot with { Subscriptions = [.. Snapshot.Subscriptions.Where(s => s.Id != refused.Id)] }, Changed());
        _started.RemoveWhere(key => key.Subscription == refused.Id);
        foreach (var key in _batchTraces.Keys.Where(key => key.Subscription == refused.Id).ToArray()) { _batchTraces.Remove(key); }
        await WatchSourcesAsync();
    }

    private static bool IsRefusal(Exception error) => error is UnauthorizedAccessException or UntrustedCallerException;

    // A source is watched as each program subscribed to it, stamped as that program's behaviors' calls are, so
    // its access policy and its own guards judge a subscription as they judge a call, whichever turn renews it.
    // What the source answers is the program's verdict on this activation: only an accepted program's
    // subscriptions are buffered, whatever watch keeps the observer registered. One source that cannot be
    // watched (its type left the host with an older program, or it refuses a program) must not starve every
    // other source or program: it is logged and skipped, unless it refuses the subscription being made right
    // now, which fails once every other program is watched.
    private async Task WatchAsync(string key, INeuron source, INeuronObserver observer, Subscription? required)
    {
        var subscribed = Snapshot.Subscriptions.Where(s => s.Neuron == key).ToArray();
        var signals = subscribed.Select(s => s.Signal).Distinct(StringComparer.Ordinal).ToArray();
        var programs = subscribed.Select(s => Find(s.Behavior)).OfType<ProgramAssignment>().DistinctBy(p => p.Owner).ToArray();
        foreach (var gone in _accepted.Keys.Where(verdict => verdict.Source == key && programs.All(p => p.Owner != verdict.Program)).ToArray())
        { _accepted.Remove(gone); }
        var requiredBy = required is not null && required.Neuron == key ? Find(required.Behavior)?.Owner : null;
        ExceptionDispatchInfo? refusal = null;
        foreach (var program in programs)
        {
            var caller = new BehaviorIdentity(program.Principal, program.Owner).Caller;
            try
            {
                await AsProgram(caller, () => source.Watch(observer, signals));
                _accepted[(key, program.Owner)] = caller;
                _unwatchable.Remove((key, program.Owner));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _accepted.Remove((key, program.Owner));
                if (program.Owner == requiredBy) { refusal = ExceptionDispatchInfo.Capture(error); }
                // Logged once per activation, source and program: the renewal timer retries it every period.
                else if (_unwatchable.Add((key, program.Owner))) { logger.LogWarning(error, "Brain runtime {Runtime} could not watch {Source} for {Program}; retrying quietly.", this.GetPrimaryKeyString(), key, program.Owner); }
                else { logger.LogDebug(error, "Brain runtime {Runtime} still cannot watch {Source} for {Program}.", this.GetPrimaryKeyString(), key, program.Owner); }
            }
        }
        refusal?.Throw();
    }

    // A call on a source carries the program's caller and nothing of the turn's own: no caller when the program
    // has none. RequestContext is async-local, so the turn's caller is back once this returns.
    private static async Task AsProgram(CallerContext? caller, Func<Task> call)
    {
        if (caller is not null) { CallerContextStamper.Stamp(caller); }
        else { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
        await call();
    }

    // A verdict holds for the caller it was given to: a program that now speaks for another principal is fed
    // nothing until its source accepts it again.
    private bool Accepted(Subscription subscription)
        => Find(subscription.Behavior) is { } program && _accepted.TryGetValue((subscription.Neuron, program.Owner), out var caller)
            && caller == new BehaviorIdentity(program.Principal, program.Owner).Caller;

    // Per source and program, the caller the source last accepted a watch from on this activation (null: the
    // program has none). Memory only: a reactivated runtime asks every source again before it buffers anything for it.
    private readonly Dictionary<(string Source, string Program), CallerContext?> _accepted = [];
    private readonly HashSet<(string Source, string Program)> _unwatchable = [];

    // One watch per distinct source across every behavior; returns the sources no subscription names any more.
    // Sources keep observers for a lease. The renewal timer interleaves and only starts lanes, so a renewal never
    // holds the runtime's turn; it also keeps this activation alive.
    private string[] SyncWatched()
    {
        var sources = Snapshot.Subscriptions.Select(s => s.Neuron).Distinct(StringComparer.Ordinal).ToArray();
        var dropped = _watched.Keys.Except(sources, StringComparer.Ordinal).ToArray();
        foreach (var gone in dropped) { _watched.Remove(gone); }
        foreach (var source in sources.Where(source => !_watched.ContainsKey(source)))
        { _watched[source] = GrainFactory.GetGrain<INeuron>(GrainId.Parse(source)); }
        if (_watched.Count == 0) { _watchRenewal?.Dispose(); _watchRenewal = null; }
        else
        {
            _watchRenewal ??= this.RegisterGrainTimer(_ => RenewWatches(),
                new GrainTimerCreationOptions { DueTime = ObserverRenewal, Period = ObserverRenewal, KeepAlive = true, Interleave = true });
        }
        return dropped;
    }

    private Task RenewWatches()
    {
        foreach (var key in _watched.Keys.ToArray()) { _ = WatchOffTurn(key, again: false); }
        return Task.CompletedTask;
    }
}
