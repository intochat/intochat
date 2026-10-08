using System.Text.Json;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Assemblies;
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

// A brain's behaviors, their durable subscriptions and the one sandbox session that runs them. Every
// change goes through the same rules: a session is wanted while anything is assigned; the host is told
// the difference over its stream; signals buffer until the host acknowledges them. The reminder is the
// backstop that relaunches a lost session, not the detector.
[GrainType("microsoft.csharp.runtime")]
internal sealed class BrainRuntimeNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<BrainRuntimeState> store,
    SessionRunner runner,
    SessionTokens tokens,
    SessionStreams streams,
    IBehaviorAssemblies assemblies,
    ScriptContracts contracts,
    IReminderRegistry reminders,
    TimeProvider time,
    IOptions<CSharpOptions> options,
    IOptions<CSharpDeploymentSettings> deployment,
    ILogger<BrainRuntimeNeuron> logger)
    : Neuron<BrainRuntimeState>(store), IBrainRuntime, IBrainRuntimeEdge, IBrainRuntimeArrivals, IRemindable, INeuronObserver
{
    internal const string ReconcileReminder = "reconcile";
    internal const int MaximumFailures = 5;
    // Orleans reminders fire at most once a minute.
    private static readonly TimeSpan ReconcilePeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromMinutes(2);
    // Crashes further apart than this are not a streak; the count starts again.
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AssemblyUrlLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LogsCap = TimeSpan.FromSeconds(10);
    // Rotated well inside SessionTokens.Lifetime, so a reminder that fires late still beats expiry.
    private static readonly TimeSpan TokenRotation = TimeSpan.FromMinutes(40);
    // Development sandboxes reach the brain on the Docker host.
    internal const string DockerHost = "host.docker.internal";

    private readonly Dictionary<string, INeuron> _watched = new(StringComparer.Ordinal);
    private IGrainTimer? _watchRenewal;
    // The tail of this activation's sandbox calls; each runs after the one before it, whatever its outcome.
    private Task _sandboxCalls = Task.CompletedTask;

    private string RuntimeId => this.GetPrimaryKeyString();
    private string Brain => RuntimeId[..RuntimeId.LastIndexOf('/')];

    // The edge validated the session token before any of these; each re-checks the session id itself.
    public override NeuronAccess Access(string operation) =>
        operation is nameof(Authorize) or nameof(Identity) or nameof(Connected) or nameof(Heartbeat) or nameof(Subscribed)
            or nameof(Acknowledge) or nameof(Reported) or nameof(PendingBatches) or nameof(Assignments)
            ? NeuronAccess.PublicOperation
            : base.Access(operation);

    public Task<BrainRuntimeSnapshot> Inspect(CancellationToken cancellationToken = default)
        => Task.FromResult(new BrainRuntimeSnapshot(Snapshot.Session?.Status ?? SessionStatus.None, Snapshot.Session?.Id,
            Snapshot.Session?.Fingerprint, Snapshot.Session?.LastHeartbeat, [.. Snapshot.Behaviors.Select(View)]));

    public async Task Touch(CancellationToken cancellationToken = default)
    {
        if (Snapshot.Behaviors.Length > 0) { await EnsureSessionAsync(); }
    }

    public async Task Assign(string behavior, string hash, Dictionary<string, string> settings, CallerContext? owner, string? appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(behavior);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(settings);
        var assignment = new BehaviorAssignment(behavior, hash, new(settings, StringComparer.Ordinal), owner, appId, CSharpFileStatus.Starting, null, 0);
        await Save(Snapshot with { Behaviors = [.. Snapshot.Behaviors.Where(existing => existing.Id != behavior), assignment] }, Changed());
        if (Snapshot.Session is null) { await EnsureSessionAsync(); }
        else { streams.TryPush(Snapshot.Session.Id, AssignmentEvent.Kind, await AssignmentFor(assignment)); }
    }

    public async Task Retire(string behavior)
    {
        if (Snapshot.Behaviors.All(existing => existing.Id != behavior)) { return; }
        await Save(Snapshot with
        {
            Behaviors = [.. Snapshot.Behaviors.Where(existing => existing.Id != behavior)],
            Subscriptions = [.. Snapshot.Subscriptions.Where(subscription => subscription.Behavior != behavior)],
        }, Changed());
        await WatchSourcesAsync();
        if (Snapshot.Session is { } session) { streams.TryPush(session.Id, RetireEvent.Kind, new RetireEvent(behavior)); }
        if (Snapshot.Behaviors.Length == 0) { await EndSessionAsync(); }
    }

    public Task<BehaviorView?> Describe(string behavior)
        => Task.FromResult(Snapshot.Behaviors.FirstOrDefault(existing => existing.Id == behavior) is { } found ? View(found) : null);

    public Task<string> Logs(string behavior, int tail)
        => Snapshot.Session is { Status: SessionStatus.Connected } session
            ? streams.RequestLogsAsync(session.Id, behavior, Math.Clamp(tail, 1, 5000), LogsCap)
            : Task.FromResult("");

    public Task<SessionAuthorization> Authorize(string session)
        => Task.FromResult(new SessionAuthorization(Speaks(session), Brain));

    public Task<BehaviorIdentity?> Identity(string session, string behavior)
        => Task.FromResult(Speaks(session) && Snapshot.Behaviors.FirstOrDefault(existing => existing.Id == behavior) is { } found
            ? new BehaviorIdentity(found.Owner, found.AppId)
            : null);

    // A host built from other contracts must not load behaviors compiled against these: it is refused,
    // stopped, and the reason stays in the session record.
    public async Task Connected(string session, string fingerprint)
    {
        RequireSpeaking(session);
        if (!ContractsFingerprint.Covers(fingerprint, contracts.Fingerprint))
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
        if (Snapshot.Behaviors.All(existing => existing.Id != behavior)) { throw new ArgumentException($"{behavior} is not assigned to this brain.", nameof(behavior)); }
        if (!GrainId.TryParse(neuron, out _)) { throw new ArgumentException($"'{neuron}' is not a neuron id such as time.timer/tea.", nameof(neuron)); }
        // Watching itself would deadlock this non-reentrant grain and self-feed on every save.
        if (neuron == this.GetGrainId().ToString()) { throw new ArgumentException("A behavior cannot subscribe to its brain runtime.", nameof(neuron)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        var existing = Snapshot.Subscriptions.FirstOrDefault(s => s.Behavior == behavior && s.Neuron == neuron && s.Signal == signal);
        if (existing is null)
        {
            existing = new(Guid.NewGuid().ToString("N"), behavior, neuron, signal, [], 0, 0);
            await Save(Snapshot with { Subscriptions = [.. Snapshot.Subscriptions, existing] }, Changed());
            await WatchSourcesAsync();
        }
        else
        {
            // Persistence can succeed before source registration fails. A retry is ready only
            // after the source has accepted the current selection, including the saved addition.
            await GrainFactory.GetGrain<INeuron>(GrainId.Parse(neuron)).Watch(this.AsReference<INeuronObserver>(),
                Snapshot.Subscriptions.Where(s => s.Neuron == neuron).Select(s => s.Signal).Distinct(StringComparer.Ordinal).ToArray());
            // A reloaded behavior gets what waited while it was down without another arrival.
            if (existing.Pending.Length > 0 && Snapshot.Session is { Status: SessionStatus.Connected })
            { streams.TryPush(session, SignalBatchEvent.Kind, Batch(existing)); }
        }
        return existing.Id;
    }

    public async Task Acknowledge(string session, string subscription, long watermark)
    {
        RequireSpeaking(session);
        var found = Snapshot.Subscriptions.FirstOrDefault(s => s.Id == subscription);
        if (found is null) { return; }
        var trimmed = found.Acknowledge(watermark);
        if (trimmed.Delivered == found.Delivered) { return; }
        await Save(Snapshot with { Subscriptions = [.. Snapshot.Subscriptions.Select(s => s == found ? trimmed : s)] }, Changed());
    }

    // Failures are per behavior: a crashing one is reloaded after a backoff until it has failed
    // MaximumFailures times in a row (each within FailureWindow of the last), then disarmed and unloaded
    // while the rest of the brain keeps running.
    public async Task Reported(string session, BehaviorStatusReport status)
    {
        RequireSpeaking(session);
        ArgumentNullException.ThrowIfNull(status);
        var found = Snapshot.Behaviors.FirstOrDefault(existing => existing.Id == status.Behavior && existing.Hash == status.Hash);
        if (found is null || found.Status == CSharpFileStatus.Disarmed) { return; }
        var failed = status.Error is not null || status.Status == nameof(CSharpFileStatus.Exited) && status.ExitCode is not 0;
        var now = time.GetUtcNow();
        var inARow = found.LastFailure is { } last && now - last < FailureWindow;
        var failures = failed ? (inARow ? found.Failures : 0) + 1 : found.Failures;
        var next = found with
        {
            Status = failures >= MaximumFailures ? CSharpFileStatus.Disarmed
                : Enum.TryParse<CSharpFileStatus>(status.Status, out var reported) ? reported : found.Status,
            ExitCode = status.ExitCode,
            Failures = failures,
            LastFailure = failed ? now : found.LastFailure,
        };
        await Save(Snapshot with { Behaviors = [.. Snapshot.Behaviors.Select(existing => existing == found ? next : existing)] }, Changed());
        if (next.Status == CSharpFileStatus.Disarmed)
        {
            logger.LogWarning("Behavior {Behavior} of brain {Brain} failed {Failures} times and is disarmed: {Error}", next.Id, Brain, failures, status.Error);
            streams.TryPush(session, RetireEvent.Kind, new RetireEvent(next.Id));
        }
        else if (failed) { ReloadLater(session, next.Id, next.Hash, TimeSpan.FromSeconds(1 << (failures - 1))); }
    }

    // One-shot: reloads the behavior if, by then, the same session still runs it at the same hash.
    private void ReloadLater(string session, string behavior, string hash, TimeSpan delay)
    {
        IGrainTimer? timer = null;
        timer = this.RegisterGrainTimer(async _ =>
        {
            timer?.Dispose();
            if (Snapshot.Session?.Id == session
                && Snapshot.Behaviors.FirstOrDefault(existing => existing.Id == behavior && existing.Hash == hash) is { Status: not CSharpFileStatus.Disarmed } current)
            { streams.TryPush(session, AssignmentEvent.Kind, await AssignmentFor(current)); }
        }, new GrainTimerCreationOptions { DueTime = delay, Period = Timeout.InfiniteTimeSpan });
    }

    public Task<IReadOnlyList<SignalBatchEvent>> PendingBatches(string session)
    {
        RequireSpeaking(session);
        return Task.FromResult<IReadOnlyList<SignalBatchEvent>>([.. Snapshot.Subscriptions.Where(s => s.Pending.Length > 0).Select(subscription => Batch(subscription))]);
    }

    public async Task<IReadOnlyList<AssignmentEvent>> Assignments(string session)
    {
        RequireSpeaking(session);
        var assigned = new List<AssignmentEvent>();
        foreach (var behavior in Snapshot.Behaviors.Where(existing => existing.Status != CSharpFileStatus.Disarmed))
        { assigned.Add(await AssignmentFor(behavior)); }
        return assigned;
    }

    Task INeuronObserver.OnSignalAsync(Signal signal)
        => Snapshot.Subscriptions.Any(s => s.Signal == signal.GetType().Name && s.Neuron == signal.Publisher.Name)
            ? this.AsReference<IBrainRuntimeArrivals>().Arrived(signal)
            : Task.CompletedTask;

    public async Task Arrived(Signal signal)
    {
        var json = JsonSerializer.Serialize(signal, signal.GetType(), RuntimeProtocol.Json);
        var name = signal.GetType().Name;
        var cap = options.Value.MaximumPending;
        var arrived = new List<Subscription>();
        var next = Snapshot.Subscriptions.Select(s =>
        {
            if (s.Signal != name || s.Neuron != signal.Publisher.Name) { return s; }
            var buffered = s.Arrive(json, cap);
            arrived.Add(buffered);
            return buffered;
        }).ToArray();
        if (arrived.Count == 0) { return; }
        await Save(Snapshot with { Subscriptions = next }, Changed());
        if (Snapshot.Session is { Status: SessionStatus.Connected } session)
        {
            // The publish's trace rides along, so the behavior's reaction joins it.
            var trace = System.Diagnostics.Activity.Current?.Id;
            foreach (var subscription in arrived) { streams.TryPush(session.Id, SignalBatchEvent.Kind, Batch(subscription, trace)); }
        }
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReconcileReminder) { return; }
        if (Snapshot.Behaviors.Length == 0) { await EndSessionAsync(); return; }
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
    internal void ResumeWatches()
    {
        if (Snapshot.Subscriptions.Length > 0) { RenewSourceWatches(dueTime: TimeSpan.Zero); }
    }

    // A new session id per launch: the previous session's token stops speaking for this brain.
    // One rule for assignment, sign-in and the reminder. A missing, refused or silent session is replaced by
    // a new one (the old token stops speaking); a live one is ensured again, which the host treats as a no-op
    // unless it lost the session. Heartbeats are the detector; nothing here probes the host.
    private async Task EnsureSessionAsync()
    {
        var session = Snapshot.Session;
        if (session is null || session.Status == SessionStatus.Refused || Silent(session))
        {
            session = new(Guid.NewGuid().ToString("N"), SessionStatus.Starting, null, null, time.GetUtcNow(), time.GetUtcNow());
            await Save(Snapshot with { Session = session }, Changed());
            await reminders.RegisterOrUpdateReminder(this.GetGrainId(), ReconcileReminder, ReconcilePeriod, ReconcilePeriod);
        }
        OffTurn(runner.Ensure, new SessionStart(Brain, session.Id, tokens.Issue(Brain, session.Id), EdgeUrl()), "start");
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

    private async Task<AssignmentEvent> AssignmentFor(BehaviorAssignment behavior)
        => new(behavior.Id, behavior.Hash,
            Reachable(await assemblies.ReadUrl(behavior.Hash, AssemblyUrlLifetime, CancellationToken.None)),
            Reachable(await assemblies.ReadUrl(behavior.Hash + ".pdb", AssemblyUrlLifetime, CancellationToken.None)),
            behavior.Settings);

    // Local blob storage answers on this machine's loopback, which no sandbox can reach; the Docker host
    // can. A SAS signs the path and query, not the host, so the rewritten URL stays valid.
    private static Uri Reachable(Uri url) => url.IsLoopback ? new UriBuilder(url) { Host = DockerHost }.Uri : url;

    private static SignalBatchEvent Batch(Subscription subscription, string? trace = null) => new(subscription.Id, subscription.Pending, subscription.Watermark, trace);

    private BehaviorView View(BehaviorAssignment behavior)
        => new(behavior.Id, behavior.Hash, behavior.Status, behavior.Failures,
            [.. Snapshot.Subscriptions.Where(s => s.Behavior == behavior.Id)
                .Select(s => new CSharpSubscriptionView(s.Neuron, s.Signal, s.Pending.Length, s.Delivered, s.Dropped))]);

    private BrainRuntimeChanged Changed() => new(RuntimeId);

    // The host dials the brain's own edge: configured, or this silo's HTTP address on the Docker host.
    private string EdgeUrl()
    {
        var root = deployment.Value.EdgeUrl is { Length: > 0 } configured ? configured : ListeningUrl();
        return root.TrimEnd('/') + "/brains/" + Uri.EscapeDataString(Brain) + "/";
    }

    private string ListeningUrl()
    {
        var listening = ServiceProvider.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Configure {CSharpDeploymentSettings.SectionName}:EdgeUrl; this brain listens on no HTTP address a sandbox can reach.");
        var wildcard = listening.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal)
            .Replace("[::]", "localhost", StringComparison.Ordinal).Replace("0.0.0.0", "localhost", StringComparison.Ordinal);
        return new UriBuilder(wildcard) { Host = DockerHost }.Uri.ToString();
    }

    private async Task WatchSourcesAsync()
    {
        var previous = _watched.Values.ToArray();
        RenewSourceWatches(dueTime: ObserverRenewal);
        var observer = this.AsReference<INeuronObserver>();
        foreach (var source in previous.Where(source => !_watched.ContainsValue(source))) { await source.Unwatch(observer); }
        foreach (var (key, source) in _watched)
        { await source.Watch(observer, Snapshot.Subscriptions.Where(s => s.Neuron == key).Select(s => s.Signal).Distinct(StringComparer.Ordinal).ToArray()); }
    }

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
                foreach (var (key, source) in watched)
                { await source.Watch(observer, Snapshot.Subscriptions.Where(s => s.Neuron == key).Select(s => s.Signal).Distinct(StringComparer.Ordinal).ToArray()); }
            },
            // Serialize renewal with subscription changes so an old selection cannot overwrite a new one.
            new GrainTimerCreationOptions { DueTime = dueTime, Period = ObserverRenewal, KeepAlive = true });
    }
}
