using System.Diagnostics;
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
using Orleans.Timers;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// A brain's programs, their durable subscriptions and the one sandbox session that runs them. Every
// change goes through the same rules: a session is wanted while anything is assigned; the host is told
// the difference over its stream; signals buffer until the host acknowledges them. The reminder is the
// backstop that relaunches a lost session, not the detector. What the synapses' runs do becomes facts that
// the brain's runs neuron publishes, one hand-off per turn after the turn's write.
[GrainType(GrainTypeName)]
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
    : Neuron<BrainRuntimeState>(store), IBrainRuntime, IBrainRuntimeEdge, IBrainPrograms, IBrainRuntimeArrivals, IBrainRuntimeSession, IRemindable, INeuronObserver
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
    private readonly Dictionary<(string Subscription, long Watermark), Trace> _batchTraces = new();
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

    private string RuntimeId => this.GetPrimaryKeyString();
    private string Brain => RuntimeId[..RuntimeId.LastIndexOf('/')];

    // The host composer may not have wired a meter yet; a brain without one simply does not price CPU.
    private ICostMeter? Meter => _meter ??= ServiceProvider.GetService<ICostMeter>();

    // A subscription to any brain's runs neuron is a reaction to run facts, and its own runs publish none: a
    // reaction to its own brain's runs would feed itself, and two brains reacting to each other's would feed each other.
    private static bool IsRunFacts(string neuron) => GrainId.Parse(neuron).Type.ToString() == BrainRunsNeuron.GrainTypeName;

    // The traces still held for pushed batches; a settled batch lets go of its trace.
    internal int TracedBatches => _batchTraces.Count;

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

    public async Task<ProgramView> Assign(string owner, Dictionary<string, string> sources, Dictionary<string, string> settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        var compiled = compiler.Compile(sources);
        if (!compiled.Success)
        {
            throw new InvalidOperationException("The program does not compile: "
                + string.Join("; ", compiled.Errors.Select(error => $"{error.File} line {error.Line} {error.Id}: {error.Message}")));
        }
        foreach (var behavior in compiled.Assemblies.Values) { await assemblies.Put(behavior, CancellationToken.None); }
        var behaviors = compiled.Assemblies.OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => new BehaviorAssembly(file.Key, file.Value.Hash)).ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", behaviors.Select(b => b.Path + ":" + b.Hash)))));
        var principal = CallerContextStamper.TryGet(out var caller) ? caller
            : Snapshot.Programs.FirstOrDefault(p => p.Owner == owner)?.Principal;
        var program = new ProgramAssignment(owner, hash, new(settings, StringComparer.Ordinal), principal, behaviors);
        var previous = Snapshot.Programs.FirstOrDefault(p => p.Owner == owner);
        var removed = previous?.Behaviors.Select(b => b.Id(owner)).Except(behaviors.Select(b => b.Id(owner)), StringComparer.Ordinal).ToArray() ?? [];
        await Save(Snapshot with
        {
            Programs = [.. Snapshot.Programs.Where(p => p.Owner != owner), program],
            Subscriptions = [.. Snapshot.Subscriptions.Where(s => !removed.Contains(s.Behavior, StringComparer.Ordinal))],
        }, Changed());
        foreach (var gone in removed) { Forget(gone); }
        if (removed.Length > 0) { await WatchSourcesAsync(); }
        if (Snapshot.Session is null) { await EnsureSessionAsync(); }
        else
        {
            foreach (var gone in removed) { streams.TryPush(Snapshot.Session.Id, RetireEvent.Kind, new RetireEvent(gone)); }
            foreach (var behavior in behaviors) { streams.TryPush(Snapshot.Session.Id, AssignmentEvent.Kind, await AssignmentFor(program, behavior)); }
        }
        return View(program);
    }

    public async Task Retire(string owner)
    {
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
            await WatchSourcesAsync(required: neuron);
            // A runs neuron retains nothing, so a reaction to run facts has no backlog to seed.
            var seeded = IsRunFacts(neuron) ? [] : await SeedRetainedAsync(neuron, signal, existing.Watermark);
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
            await GrainFactory.GetGrain<INeuron>(GrainId.Parse(neuron)).Watch(this.AsReference<INeuronObserver>(),
                Snapshot.Subscriptions.Where(s => s.Neuron == neuron).Select(s => s.Signal).Distinct(StringComparer.Ordinal).ToArray());
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
                group.Sum(ack => ack.WallMicroseconds), group.Sum(ack => ack.Batches)))]);
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
                RunOutcome.Succeeded, report.Batches, report.WallMicroseconds, report.CpuMicroseconds, null), traces[report.Subscription]);
        }
        Flush();
        if (Meter is not { } meter) { return; }
        // A replayed ack prices nothing again. Metering observes the run: a meter that fails does not fail the ack.
        foreach (var (before, after, report) in advanced.Where(run => run.Report.CpuMicroseconds > 0))
        {
            using var entered = traces[report.Subscription] is { } batch ? TraceContext.Enter(batch) : null;
            try
            {
                await meter.RecordAsync(CostKind.Script, "cpu-ms", report.CpuMicroseconds / 1000m, "ms", Funding.Local,
                    $"{report.Subscription}:{report.Watermark}", CancellationToken.None, new CostSubject(Synapse: before.Behavior, App: Find(before.Behavior)?.Owner));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Brain {Brain} could not price {Behavior}'s run through {Watermark}.", Brain, before.Behavior, after.Delivered); }
        }
    }

    // The batches of a subscription at or below an acknowledged watermark are settled: none is in flight any
    // more. The trace of the latest of them is the run's.
    private Trace? Settle(string subscription, long watermark)
    {
        Trace? latest = null;
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

    Task INeuronObserver.OnSignalAsync(Signal signal)
        => Snapshot.Subscriptions.Any(s => s.Signal == signal.GetType().Name && s.Neuron == signal.Publisher.Name)
            ? this.AsReference<IBrainRuntimeArrivals>().Arrived(signal)
            : Task.CompletedTask;

    public async Task Arrived(Signal signal)
    {
        _facts.Clear();
        if (await CatchUpAsync(signal.Publisher.Name)) { Flush(); return; }
        var json = JsonSerializer.Serialize(signal, signal.GetType(), RuntimeProtocol.Json);
        var name = signal.GetType().Name;
        var cap = options.Value.MaximumPending;
        var arrived = new List<Subscription>();
        var drops = new List<(Subscription Buffered, long Count)>();
        var next = Snapshot.Subscriptions.Select(s =>
        {
            if (s.Signal != name || s.Neuron != signal.Publisher.Name) { return s; }
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

    // On a host with a part log a live signal is a nudge: the source's log after each watermark is what is buffered,
    // so a signal the runtime never heard live arrives too and none arrives twice. False when nothing is journaled.
    public async Task CatchUp()
    {
        _facts.Clear();
        foreach (var neuron in Snapshot.Subscriptions.Select(s => s.Neuron).Distinct(StringComparer.Ordinal).ToArray())
        {
            try { await CatchUpAsync(neuron); }
            catch (Exception error)
            { logger.LogWarning(error, "Brain {Brain} could not read {Neuron}'s part log from its watermark; live signals and the next re-subscribe retry.", Brain, neuron); }
        }
        Flush();
    }

    private async Task<bool> CatchUpAsync(string neuron, bool push = true)
    {
        var matching = Snapshot.Subscriptions.Where(s => s.Neuron == neuron).ToArray();
        if (matching.Length == 0) { return true; }
        var since = matching.Min(s => s.SourceSeq);
        if (await journal.SignalsOf(GrainId.Parse(neuron), since) is not { } read) { return false; }
        var cap = options.Value.MaximumPending;
        var arrived = new List<Subscription>();
        var drops = new List<(Subscription Buffered, long Count)>();
        var changed = false;
        var next = Snapshot.Subscriptions.Select(s =>
        {
            if (s.Neuron != neuron) { return s; }
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
        var before = Snapshot.Session?.Id;
        await EnsureSessionAsync();
        if (Snapshot.Session is not { } session || session.Id != before) { return; }
        // Rotated only once the host has it; a rotation its stream missed is retried on the next reminder.
        if (session.TokenIssued < time.GetUtcNow() - TokenRotation
            && streams.TryPush(session.Id, TokenEvent.Kind, new TokenEvent(tokens.Issue(Brain, session.Id))))
        { await Save(Snapshot with { Session = session with { TokenIssued = time.GetUtcNow() } }, Changed()); }
    }

    // A source's own signal may be what activated this runtime, so watching it now would deadlock;
    // the renewal timer's first tick watches it right after activation instead.
    internal Task ResumeAsync()
    {
        if (Snapshot.Subscriptions.Length > 0)
        {
            RenewSourceWatches(dueTime: TimeSpan.Zero);
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
        if (trace is not null) { _batchTraces[(subscription.Id, subscription.Watermark)] = trace; }
        return new SignalBatchEvent(subscription.Id, subscription.Pending, subscription.Watermark,
            trace is null ? System.Diagnostics.Activity.Current?.Id : $"00-{trace.TraceId}-{System.Diagnostics.ActivitySpanId.CreateRandom().ToHexString()}-01");
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
            subscription.Signal, subscription.Watermark), _batchTraces.GetValueOrDefault((subscription.Id, subscription.Watermark)) ?? TraceContext.Current));
    }

    // Signals pushed out of a full buffer never reach the synapse: a run that cannot happen, counted, never silent.
    private void Dropped(Subscription buffered, long count)
    {
        if (IsRunFacts(buffered.Neuron) || Synapse(buffered.Behavior) is not { } synapse) { return; }
        Ran(new BehaviorRan(RuntimeId, synapse.Owner, buffered.Behavior, synapse.Hash, buffered.Neuron, buffered.Signal, buffered.Delivered,
            RunOutcome.Dropped, (int)Math.Min(count, int.MaxValue), 0, 0, null), null);
    }

    private void Ran(BehaviorRan ran, Trace? trace)
    {
        trace ??= TraceContext.Current;
        _facts.Add(new(ran, trace));
        var counts = _runs.GetValueOrDefault(ran.Behavior);
        _runs[ran.Behavior] = ran.Outcome switch
        {
            RunOutcome.Succeeded => counts with { Runs = counts.Runs + 1, LastDurationMicroseconds = ran.DurationMicroseconds },
            RunOutcome.Failed => counts with { Runs = counts.Runs + 1, Failures = counts.Failures + 1 },
            _ => counts,
        };
        logger.LogInformation("BehaviorRan {Behavior} {Outcome} {DurationMicroseconds}", ran.Behavior, ran.Outcome, ran.DurationMicroseconds);
        Span(ran, trace);
    }

    // The run as a span in the batch's own trace, ending now and as long as the synapse took.
    private void Span(BehaviorRan ran, Trace? trace)
    {
        var end = time.GetUtcNow();
        var parent = trace is { TraceId: { Length: 32 } id } && id.All(char.IsAsciiHexDigitLower)
            ? new ActivityContext(ActivityTraceId.CreateFromString(id), default, ActivityTraceFlags.Recorded)
            : default;
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

    // A source that retains the signal seeds a new subscription with the backlog the synapse missed.
    // A source without retention contributes nothing: the cast fails and there is nothing to seed.
    private async Task<string[]> SeedRetainedAsync(string neuron, string signal, long since)
    {
        try
        {
            var retained = await GrainFactory.GetGrain<IRetainsSignals>(GrainId.Parse(neuron)).Retained(signal, since);
            return [.. retained.Select(item => JsonSerializer.Serialize(item.Signal, item.Signal.GetType(), RuntimeProtocol.Json))];
        }
        catch (InvalidCastException)
        {
            return [];
        }
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

    private async Task WatchSourcesAsync(string? required = null)
    {
        var previous = _watched.Values.ToArray();
        RenewSourceWatches(dueTime: ObserverRenewal);
        var observer = this.AsReference<INeuronObserver>();
        foreach (var source in previous.Where(source => !_watched.ContainsValue(source)))
        {
            // A source that is gone (its type left the host) cannot be unwatched either; its lease simply lapses.
            try { await source.Unwatch(observer); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogDebug(error, "Brain runtime {Runtime} could not unwatch {Source}.", this.GetPrimaryKeyString(), source.GetGrainId()); }
        }
        foreach (var (key, source) in _watched) { await WatchAsync(key, source, observer, rethrow: key == required); }
    }

    // One source that cannot be watched (its type left the host with an older program) must not starve
    // every other source: it is logged and skipped, unless it is the one being subscribed right now.
    private async Task WatchAsync(string key, INeuron source, INeuronObserver observer, bool rethrow)
    {
        try
        {
            await source.Watch(observer, Snapshot.Subscriptions.Where(s => s.Neuron == key).Select(s => s.Signal).Distinct(StringComparer.Ordinal).ToArray());
            _unwatchable.Remove(key);
        }
        catch (Exception error) when (!rethrow && error is not OperationCanceledException)
        {
            // Logged once per activation and source: the renewal timer retries it every period.
            if (_unwatchable.Add(key)) { logger.LogWarning(error, "Brain runtime {Runtime} could not watch {Source}; retrying quietly.", this.GetPrimaryKeyString(), key); }
            else { logger.LogDebug(error, "Brain runtime {Runtime} still cannot watch {Source}.", this.GetPrimaryKeyString(), key); }
        }
    }

    private readonly HashSet<string> _unwatchable = new(StringComparer.Ordinal);

    // One watch per distinct source across every behavior. Sources keep observers for a lease; renewing
    // on a timer also keeps this activation alive.
    private void RenewSourceWatches(TimeSpan dueTime)
    {
        var sources = Snapshot.Subscriptions.Select(s => s.Neuron).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var gone in _watched.Keys.Except(sources).ToArray()) { _watched.Remove(gone); }
        foreach (var source in sources.Where(source => !_watched.ContainsKey(source)))
        { _watched[source] = GrainFactory.GetGrain<INeuron>(GrainId.Parse(source)); }
        var observer = this.AsReference<INeuronObserver>();
        var watched = _watched.ToArray();
        _watchRenewal?.Dispose();
        _watchRenewal = watched.Length == 0 ? null : this.RegisterGrainTimer(
            async _ =>
            {
                foreach (var (key, source) in watched) { await WatchAsync(key, source, observer, rethrow: false); }
            },
            // Serialize renewal with subscription changes so an old selection cannot overwrite a new one.
            new GrainTimerCreationOptions { DueTime = dueTime, Period = ObserverRenewal, KeepAlive = true });
    }
}
