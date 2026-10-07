using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Client.Edge.V1;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files.Signals;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using Microsoft.Extensions.Options;
using Orleans.Timers;

namespace DigitalBrain.Microsoft.CSharp.Files;

// Start records the intent to keep the program running; the reconcile (a reminder, or a signal arriving
// for a subscription) decides, in one place, whether a run is due. A program that holds no subscription
// is due until it exits cleanly; a program that holds subscriptions is due while something is pending.
[GrainType("microsoft.csharp.file")]
internal sealed partial class CSharpFileNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<CSharpFileState> store,
    ICSharpRunner runner,
    ScriptRunEnvironment environment,
    IReminderRegistry reminders,
    IOptions<CSharpOptions> options)
    : Neuron<CSharpFileState>(store), ICSharpFile, ICSharpAppBinding, ICSharpFileArrivals, ICSharpFileEdge, IRemindable, INeuronObserver
{
    public override NeuronAccess Access(string operation) =>
        // The script edge's inbound operations authenticate with the run token the edge has already
        // verified; the neuron re-checks the run id itself. Same rule as a webhook.
        operation is nameof(ICSharpFileEdge.Authorize) or nameof(ICSharpFileEdge.Subscribed)
            or nameof(ICSharpFileEdge.DrainPending) or nameof(ICSharpFileEdge.Acknowledge)
            ? NeuronAccess.PublicOperation
            : base.Access(operation);

    internal const int MaximumSourceBytes = 128 * 1024;
    internal const int MaximumFailures = 5;
    internal const string ReconcileReminder = "reconcile";
    // Orleans reminders fire at most once a minute.
    internal static readonly TimeSpan ReconcilePeriod = TimeSpan.FromMinutes(1);
    private const int MaximumSettings = 64;
    private readonly Dictionary<string, INeuron> _watched = new(StringComparer.Ordinal);
    private IGrainTimer? _watchRenewal;
    private string FileId => this.GetPrimaryKeyString();

    private enum Verdict { Wait, Finished, Run }

    internal void RequireBoundAccess(GrainId? source, string method)
    {
        if (Snapshot.AppId is not { } app) { return; }
        if (Snapshot.AppBrain != BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("This behavior belongs to another brain."); }
        if (method is nameof(Write) or nameof(Configure) or nameof(Start) or nameof(Swap) or nameof(Stop) or nameof(Delete)
            && (source?.Type.ToString() != DigitalBrain.Framework.AppGrainType.Name || source?.Key.ToString() != app))
        { throw new UnauthorizedAccessException("Only the owning Apps grain may change a bound behavior."); }
    }

    public async Task BindApp(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var brain = BrainScope.CurrentId();
        if (Snapshot.AppId is not null && (Snapshot.AppId != appId || Snapshot.AppBrain != brain))
        { throw new UnauthorizedAccessException("The behavior is already bound to another app."); }
        if (Snapshot.AppId is not null) { return; }
        await StopCurrentRunAsync(CancellationToken.None);
        await Save(Snapshot with { AppId = appId, AppBrain = brain, Run = null, Subscriptions = [] }, new CSharpFileChanged(FileId));
    }

    // A source's own signal may be what activated this file, so watching it now would deadlock;
    // the renewal timer's first tick watches it right after activation instead.
    internal void ResumeWatches()
    {
        if (Snapshot.Run is { Active: true } && Snapshot.Subscriptions.Length > 0) { RenewSourceWatches(dueTime: TimeSpan.Zero); }
    }

    public async Task<CSharpFileSnapshot> Inspect(CancellationToken cancellationToken = default)
        => Describe(await StateAsync(cancellationToken));

    public Task Write(string program, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        if (Encoding.UTF8.GetByteCount(program) > MaximumSourceBytes) { throw new ArgumentException($"Source exceeds {MaximumSourceBytes} bytes.", nameof(program)); }
        return Save(Snapshot with { Program = program }, new CSharpFileChanged(FileId));
    }

    public Task Configure(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count > MaximumSettings) { throw new ArgumentException($"At most {MaximumSettings} settings are allowed.", nameof(settings)); }
        if (settings.Keys.FirstOrDefault(name => !SettingName().IsMatch(name)) is { } invalid)
        { throw new ArgumentException($"Setting '{invalid}' must contain only letters, digits and underscores.", nameof(settings)); }
        return Save(Snapshot with { Settings = new(settings, StringComparer.Ordinal) }, new CSharpFileChanged(FileId));
    }

    public async Task<CSharpFileSnapshot> Start(CancellationToken cancellationToken = default)
    {
        RequireSource();
        await UnwatchSourcesAsync();
        await StopCurrentRunAsync(cancellationToken);
        await reminders.RegisterOrUpdateReminder(this.GetGrainId(), ReconcileReminder, ReconcilePeriod, ReconcilePeriod);
        // A fresh start is a fresh intent: the new program re-registers its subscriptions by running.
        await LaunchAsync(Snapshot with { Subscriptions = [] }, FreshRun(), cancellationToken);
        return Describe(await StateAsync(cancellationToken));
    }

    public async Task<CSharpFileSnapshot> Swap(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes) { throw new ArgumentException($"Source exceeds {MaximumSourceBytes} bytes.", nameof(source)); }
        // An edit is not a fresh intent: keep the durable subscriptions, their watermarks and their
        // buffered signals, and keep watching their sources, so nothing published during the restart is
        // lost; the new run drains the buffers when it re-subscribes.
        await StopCurrentRunAsync(cancellationToken);
        await reminders.RegisterOrUpdateReminder(this.GetGrainId(), ReconcileReminder, ReconcilePeriod, ReconcilePeriod);
        await LaunchAsync(Snapshot with { Program = source }, FreshRun(), cancellationToken);
        await WatchSourcesAsync();
        return Describe(await StateAsync(cancellationToken));
    }

    public async Task<CSharpFileSnapshot> Stop(CancellationToken cancellationToken = default)
    {
        await StopReconcilingAsync();
        await UnwatchSourcesAsync();
        await StopCurrentRunAsync(cancellationToken);
        var state = await StateAsync(cancellationToken);
        await Save(Snapshot with { Run = Snapshot.Run is { } run ? run with { Active = false } : null, Subscriptions = [] }, new CSharpFileChanged(FileId));
        return Describe(state);
    }

    public Task<string> ReadLogs(int tail = 200, CancellationToken cancellationToken = default)
        => Snapshot.Run is { } run ? runner.LogsAsync(run.Owner, run.Id, tail, cancellationToken) : Task.FromResult("");

    public async Task Delete(CancellationToken cancellationToken = default)
    {
        await StopReconcilingAsync();
        await UnwatchSourcesAsync();
        await StopCurrentRunAsync(cancellationToken);
        await Save(new CSharpFileState(), new CSharpFileChanged(FileId));
        DeactivateOnIdle();
    }

    Task INeuronObserver.OnSignalAsync(Signal signal)
        => Snapshot.Subscriptions.Any(s => s.Signal == signal.GetType().Name && s.Neuron == signal.Publisher.Name)
            ? this.AsReference<ICSharpFileArrivals>().Arrived(signal)
            : Task.CompletedTask;

    // Buffers the signal on the subscription it matches, then lets the reconcile decide about a run.
    public async Task Arrived(Signal signal)
    {
        var json = JsonSerializer.Serialize(signal, signal.GetType(), ScriptEdgeProtocol.Json);
        var name = signal.GetType().Name;
        var cap = options.Value.MaximumPending;
        var matched = false;
        var overflowed = false;
        var next = Snapshot.Subscriptions.Select(s =>
        {
            if (s.Signal != name || s.Neuron != signal.Publisher.Name) { return s; }
            matched = true;
            var arrived = s.Arrive(json, cap);
            overflowed |= arrived.Dropped > s.Dropped;
            return arrived;
        }).ToArray();
        if (!matched) { return; }
        // The arrival rings the doorbell; an overflow is also a durable change subscribers can see.
        await Save(Snapshot with { Subscriptions = next }, new CSharpPendingArrived(FileId), overflowed ? new CSharpFileChanged(FileId) : null);
        await ReconcileAsync();
    }

    public async Task Subscribed(string runId, string neuron, string signal)
    {
        await RequireSpeakingRun(runId);
        if (!GrainId.TryParse(neuron, out _)) { throw new ArgumentException($"'{neuron}' is not a neuron id such as time.timer/tea.", nameof(neuron)); }
        // Watching itself would deadlock this non-reentrant grain and self-feed on every save.
        if (neuron == this.GetGrainId().ToString()) { throw new ArgumentException("A script cannot subscribe to its own file.", nameof(neuron)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        if (!Snapshot.Subscriptions.Any(existing => existing.Neuron == neuron && existing.Signal == signal))
        {
            await Save(Snapshot with { Subscriptions = [.. Snapshot.Subscriptions, new(neuron, signal, [], 0, 0)] }, new CSharpFileChanged(FileId));
        }
        await WatchSourcesAsync();
    }

    public async Task<PendingBatch> DrainPending(string runId, string neuron, string signal)
    {
        await RequireSpeakingRun(runId);
        var subscription = Snapshot.Subscriptions.FirstOrDefault(s => s.Neuron == neuron && s.Signal == signal);
        return subscription is null ? new([], 0) : new(subscription.Pending, subscription.Watermark);
    }

    public async Task Acknowledge(string runId, string neuron, string signal, long watermark)
    {
        await RequireSpeakingRun(runId);
        var subscription = Snapshot.Subscriptions.FirstOrDefault(s => s.Neuron == neuron && s.Signal == signal);
        if (subscription is null) { return; }
        var trimmed = subscription.Acknowledge(watermark);
        if (trimmed.Delivered == subscription.Delivered) { return; }
        await Save(Snapshot with
        {
            Subscriptions = Snapshot.Subscriptions.Select(s => s == subscription ? trimmed : s).ToArray(),
        }, new CSharpFileChanged(FileId));
    }

    private async Task RequireSpeakingRun(string runId)
    {
        var authorization = await Authorize(runId);
        if (!authorization.Active) { throw new UnauthorizedAccessException("This run no longer speaks for its file."); }
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
        => reminderName == ReconcileReminder ? ReconcileAsync() : Task.CompletedTask;

    // The one place a run is decided. Runs are not retried on a schedule of their own: a failure counts
    // when the next run is due, and after MaximumFailures the file disarms instead of burning a run on
    // every signal or tick.
    private async Task ReconcileAsync()
    {
        if (Snapshot.Run is not { Active: true } run) { await StopReconcilingAsync(); return; }
        if (_watched.Count == 0 && Snapshot.Subscriptions.Length > 0) { await WatchSourcesAsync(); }
        var state = await runner.InspectAsync(run.Owner, run.Id, CancellationToken.None);
        switch (Judge(state))
        {
            case Verdict.Wait:
                return;
            case Verdict.Finished:
                await StopReconcilingAsync();
                await Save(Snapshot with { Run = run with { Active = false, Failures = 0 } }, new CSharpFileChanged(FileId));
                return;
        }
        var failures = state switch
        {
            { Status: CSharpFileStatus.Exited, ExitCode: not 0 } => run.Failures + 1,
            { Status: CSharpFileStatus.Exited } => 0,
            // The run or its whole sandbox is gone; that is not the program's failure.
            _ => run.Failures,
        };
        if (failures >= MaximumFailures)
        {
            await StopReconcilingAsync();
            await UnwatchSourcesAsync();
            await Save(Snapshot with { Run = run with { Active = false, Failures = failures }, Subscriptions = [] }, new CSharpFileChanged(FileId));
            return;
        }
        await LaunchAsync(Snapshot, run with { Id = Guid.NewGuid().ToString("N"), Failures = failures }, CancellationToken.None);
    }

    private Verdict Judge(CSharpRunState state)
        => state.Status == CSharpFileStatus.Running ? Verdict.Wait
            : Snapshot.Subscriptions.Length == 0
                ? state is { Status: CSharpFileStatus.Exited, ExitCode: 0 } ? Verdict.Finished : Verdict.Run
                : Snapshot.Subscriptions.Any(s => s.Pending.Length > 0) ? Verdict.Run : Verdict.Wait;

    // The run is recorded before it launches: the sandbox can reach the edge before StartAsync returns,
    // and the edge authorizes by exactly this id. A launch that throws leaves the intent recorded, so the
    // reminder retries it.
    private async Task LaunchAsync(CSharpFileState basis, RunRecord run, CancellationToken cancellationToken)
    {
        await Save(basis with { Run = run }, new CSharpFileChanged(FileId));
        await runner.StartAsync(new CSharpRun(run.Owner, run.Id, basis.Program, environment.Create(FileId, run.Id, basis.Settings)), cancellationToken);
    }

    private static RunRecord FreshRun()
    {
        var (principal, context) = CallerContextStamper.TryGet(out var caller) ? (caller.PrincipalId, caller) : ("system", null);
        return new(Guid.NewGuid().ToString("N"), principal, context, Failures: 0, Active: true);
    }

    private Task<CSharpRunState> StateAsync(CancellationToken cancellationToken)
        => Snapshot.Run is { } run ? runner.InspectAsync(run.Owner, run.Id, cancellationToken) : Task.FromResult(CSharpRunState.Stopped);

    private async Task WatchSourcesAsync()
    {
        RenewSourceWatches(dueTime: ObserverRenewal);
        var observer = this.AsReference<INeuronObserver>();
        foreach (var source in _watched.Values) { await source.Watch(observer); }
    }

    // Sources keep observers for a lease; renewing on a timer also keeps this activation alive.
    private void RenewSourceWatches(TimeSpan dueTime)
    {
        _watched.Clear();
        foreach (var subscription in Snapshot.Subscriptions)
        {
            if (!_watched.ContainsKey(subscription.Neuron))
            { _watched[subscription.Neuron] = GrainFactory.GetGrain<INeuron>(GrainId.Parse(subscription.Neuron)); }
        }
        var observer = this.AsReference<INeuronObserver>();
        var sources = _watched.Values.ToArray();
        _watchRenewal?.Dispose();
        _watchRenewal = sources.Length == 0 ? null : this.RegisterGrainTimer(
            async _ => { foreach (var source in sources) { await source.Watch(observer); } },
            new GrainTimerCreationOptions { DueTime = dueTime, Period = ObserverRenewal, Interleave = true, KeepAlive = true });
    }

    private async Task UnwatchSourcesAsync()
    {
        _watchRenewal?.Dispose();
        _watchRenewal = null;
        if (_watched.Count == 0) { return; }
        var observer = this.AsReference<INeuronObserver>();
        var sources = _watched.Values.ToArray();
        _watched.Clear();
        foreach (var source in sources) { await source.Unwatch(observer); }
    }

    // A token speaks for exactly the current run while the file is wanted.
    public Task<RunAuthorization> Authorize(string runId)
        => Task.FromResult(new RunAuthorization(
            Snapshot.Run is { Active: true } run && runId.Length > 0 && runId == run.Id,
            Snapshot.Run?.Caller is { } owner
                ? owner with { AppId = Snapshot.AppId is { } app && Snapshot.AppBrain == BrainScope.Create(owner.AccountId, owner.BrainId).Id ? app : FileId }
                : null));

    private Task StopCurrentRunAsync(CancellationToken cancellationToken)
        => Snapshot.Run is { } run ? runner.StopAsync(run.Owner, run.Id, cancellationToken) : Task.CompletedTask;

    private async Task StopReconcilingAsync()
    {
        if (await reminders.GetReminder(this.GetGrainId(), ReconcileReminder) is { } reminder)
        {
            await reminders.UnregisterReminder(this.GetGrainId(), reminder);
        }
    }

    private void RequireSource()
    {
        if (Snapshot.AppId is not null && Snapshot.AppBrain != BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("This behavior belongs to another brain."); }
        if (string.IsNullOrWhiteSpace(Snapshot.Program)) { throw new InvalidOperationException("Write the C# source before starting it."); }
    }

    // A wanted file that is between runs reports Restarting: the reconcile will launch the next one.
    private CSharpFileSnapshot Describe(CSharpRunState state)
        => new(FileId, Snapshot.Program, Snapshot.Settings,
            Snapshot.Run is { Active: true } && Judge(state) == Verdict.Run ? CSharpFileStatus.Restarting : state.Status,
            state.ExitCode, state.StartedAt, Snapshot.Run is { Active: true }, Snapshot.Run?.Failures ?? 0)
        { Subscriptions = Snapshot.Subscriptions.Select(s => new CSharpSubscriptionView(s.Neuron, s.Signal, s.Pending.Length, s.Delivered, s.Dropped)).ToArray() };

    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex SettingName();
}
