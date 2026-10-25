using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Utilities;
using IntoChat.OS.Framework.Modules;
using DeactivationCause = DigitalBrain.DeactivationCause;
using IDeactivatingNeuron = DigitalBrain.Spi.IDeactivatingNeuron;

namespace IntoChat.OS.Framework;

// Every neuron is observable and activated through IActivationHandler<T>; OnActivateAsync is
// sealed so the runtime owns the order and the failure handling.
[BrainAffinePlacement]
public abstract class Neuron : Grain, INeuron
{
    private readonly Guid _activation = Guid.NewGuid();
    private sealed record WatchRegistration(INeuronObserver Observer, HashSet<string>? Signals);
    private ObserverManager<INeuronObserver, WatchRegistration>? _observers;
    private ObserverManager<INeuronObserver, WatchRegistration> Observers => _observers ??= new(
        Services.GetRequiredService<IOptions<ObserverOptions>>().Value.Lease,
        Services.GetRequiredService<ILoggerFactory>().CreateLogger("Neuron.Observers"));

    protected TimeSpan ObserverRenewal => TimeSpan.FromTicks(Services.GetRequiredService<IOptions<ObserverOptions>>().Value.Lease.Ticks / 5);

    // Transitional: the full grain id until keys are brain-owned. Equal to Context.Address.StorageKey; reads the
    // grain id until the provider hosts neurons (clean-core PR 5).
    public NeuronId Id => new(GrainIdentity.ToString());

    // A neuron hosted by a runtime other than Orleans (the in-memory runtime) has no grain context: the runtime
    // attaches its context before activation, and identity and services come from that context.
    private bool Hosted => ((IGrainBase)this).GrainContext is null && _context is not null;

    private protected GrainId GrainIdentity => Hosted ? GrainId.Create(_context!.Address.NeuronType, _context.Address.Key) : this.GetGrainId();

    private protected IServiceProvider Services => Hosted ? _context!.Services : ServiceProvider;

    // Public runtime hook: a runtime that hosts neurons outside Orleans (IntoChat.OS.Runtime.InMemory) calls it
    // once, before activation, with its own context. A second call throws, so a context cannot be swapped.
    public void Attach(INeuronContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null) { throw new InvalidOperationException("The neuron already has a runtime context."); }
        _context = context;
    }

    private INeuronContext? _context;

    // The runtime SPI (clean-core 2.3) while the neuron is still a grain: the neutral form of the Orleans
    // members this class and its subclasses use directly.
    protected INeuronContext Context => _context ??= new GrainNeuronContext(this, DelayDeactivation, DeactivateOnIdle, new Fanout(this));

    internal INeuronContext SpiContext => Context;

    private DeactivationCause? _cause;

    // IDeactivatingNeuron runs from the grain lifecycle, so it runs even when a subclass's OnDeactivateAsync
    // override skips base; the override below only records why (Orleans calls it before the lifecycle stops).
    protected Neuron()
    {
        if (this is IDeactivatingNeuron && ((IGrainBase)this).GrainContext is { } grain)
        {
            grain.ObservableLifecycle.Subscribe(nameof(IDeactivatingNeuron), GrainLifecycleStage.Activate, _ => Task.CompletedTask,
                cancellationToken => ((IDeactivatingNeuron)this).OnDeactivating(_cause ?? DeactivationCause.Idle, cancellationToken));
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _cause = CauseOf(reason);
        WatchRenewal[] live;
        lock (_renewals) { live = [.. _renewals]; }
        foreach (var renewal in live)
        {
            try { await renewal.Release(withdraw: reason.ReasonCode != DeactivationReasonCode.ShuttingDown); }
            catch (Exception error) { RenewalLogger.LogWarning(error, "A watch renewal of {Neuron} could not be released.", Id.Name); }
        }
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    private readonly HashSet<WatchRenewal> _renewals = [];

    private ILogger RenewalLogger => Services.GetRequiredService<ILoggerFactory>().CreateLogger("Neuron.WatchRenewal");

    // Holds a lease on `source` for this neuron: watches now, then re-watches every ObserverRenewal. A source that
    // reactivated (its activation changed) is reported to onSourceReactivated; onRenewed runs after each successful tick.
    // A failed tick logs a warning and the next tick retries. Disposing the renewal stops it and withdraws the watch.
    protected async Task<IWatchRenewal> WatchRenewing(INeuron source, string[]? signals, bool keepAlive, bool interleave = false,
        Func<Guid, Task>? onSourceReactivated = null, Func<Task>? onRenewed = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var observer = Context.Self<INeuronObserver>();
        Task<Guid> Watch() => signals is null ? source.Watch(observer) : source.Watch(observer, signals);
        var activation = await Watch();
        var logger = RenewalLogger;
        WatchRenewal? renewal = null;
        renewal = Track(new WatchRenewal(() => source.Unwatch(observer), Forget, logger));
        renewal.Run(Context.Timers.Start("watch-renewal:" + Guid.NewGuid().ToString("N"), ObserverRenewal, ObserverRenewal, async _ =>
        {
            if (renewal.Released) { return; }
            try
            {
                var current = await Watch();
                if (current != activation)
                {
                    activation = current;
                    if (onSourceReactivated is not null) { await onSourceReactivated(current); }
                }
                if (onRenewed is not null) { await onRenewed(); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "A watch renewal of {Neuron} failed; the next tick retries.", Id.Name); }
        }, interleave, keepAlive));
        return renewal;
    }

    // For a neuron whose watches need their own dispatch (a caller per watch, a lane per source): the same lease
    // timer, with `renew` re-establishing every watch. Disposing stops the timer; the neuron withdraws its own watches.
    protected IWatchRenewal WatchRenewing(Func<Task> renew, bool keepAlive, bool interleave = false)
    {
        ArgumentNullException.ThrowIfNull(renew);
        var logger = RenewalLogger;
        WatchRenewal? renewal = null;
        renewal = Track(new WatchRenewal(null, Forget, logger));
        renewal.Run(Context.Timers.Start("watch-renewal:" + Guid.NewGuid().ToString("N"), ObserverRenewal, ObserverRenewal, async _ =>
        {
            if (renewal.Released) { return; }
            try { await renew(); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "A watch renewal of {Neuron} failed; the next tick retries.", Id.Name); }
        }, interleave, keepAlive));
        return renewal;
    }

    private WatchRenewal Track(WatchRenewal renewal)
    {
        lock (_renewals) { _renewals.Add(renewal); }
        return renewal;
    }

    private void Forget(WatchRenewal renewal)
    {
        lock (_renewals) { _renewals.Remove(renewal); }
    }

    internal static DeactivationCause CauseOf(DeactivationReason reason) => reason.ReasonCode switch
    {
        DeactivationReasonCode.ActivationIdle => DeactivationCause.Idle,
        DeactivationReasonCode.ShuttingDown => DeactivationCause.Shutdown,
        DeactivationReasonCode.ApplicationRequested => DeactivationCause.Requested,
        _ => DeactivationCause.Failed,
    };

    public sealed override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        var handlers = Services.GetServices(typeof(IActivationHandler<>).MakeGenericType(GetType()));
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ((IActivationHandler)handler!).Activated(this, cancellationToken);
        }
    }

    private static readonly ConcurrentDictionary<MethodInfo, bool> OneWayTargets = new();

    // Sends a one-way call to this neuron's own reference; it completes when sent, and the deferred work's failures surface in the target turn.
    protected Task Defer<TSelf>(Expression<Func<TSelf, Task>> call) where TSelf : class
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Body is not MethodCallExpression { Object: ParameterExpression } invocation)
        { throw new ArgumentException("Defer takes a single call on the self reference, such as self => self.Method(args).", nameof(call)); }
        var method = invocation.Method;
        if (!OneWayTargets.GetOrAdd(method, static m => m.IsDefined(typeof(Orleans.Concurrency.OneWayAttribute), inherit: false) || m.IsDefined(typeof(OneWayCallAttribute), inherit: false)))
        { throw new InvalidOperationException($"Defer target {method.DeclaringType?.Name}.{method.Name} must be [OneWay]"); }
        return call.Compile()(Context.Self<TSelf>());
    }

    public virtual Task<Guid> Watch(INeuronObserver observer)
    {
        Observers.Subscribe(observer, new(observer, null));
        return Task.FromResult(_activation);
    }

    public virtual Task<Guid> Watch(INeuronObserver observer, string[] signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        foreach (var signal in signals) { ArgumentException.ThrowIfNullOrWhiteSpace(signal); }
        Observers.Subscribe(observer, new(observer, signals.ToHashSet(StringComparer.Ordinal)));
        return Task.FromResult(_activation);
    }

    public virtual Task Unwatch(INeuronObserver observer)
    {
        Observers.Unsubscribe(observer);
        return Task.CompletedTask;
    }

    // Serves this neuron's journalled signals after `since`, oldest first. A watcher filters what it wants.
    public virtual Task<SignalPage> Read(long since, int limit) => SignalPages.Read(Journal, GrainIdentity, since, limit);

    // A signal is durable before any observer sees it (Q27).
    protected async Task PublishAsync(Signal signal)
    {
        var stamped = Stamp(signal);
        await Notify([stamped], await Journal.Append(GrainIdentity, [stamped]));
    }

    // Several facts of one call become durable in one journal append before any observer sees them. Each is
    // stamped under the trace that caused it, or the current one when it names none.
    protected async Task PublishAsync((Signal Signal, Trace? Trace)[] signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (signals.Length == 0) { return; }
        var stamped = new Signal[signals.Length];
        for (var i = 0; i < signals.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(signals[i].Signal);
            using var entered = signals[i].Trace is { } trace ? TraceContext.Enter(trace) : null;
            stamped[i] = Stamp(signals[i].Signal);
        }
        await Notify(stamped, await Journal.Append(GrainIdentity, stamped));
    }

    private protected ISignalJournal Journal => Services.GetRequiredService<ISignalJournal>();

    private protected Signal Stamp(Signal signal) => signal with { Publisher = Id, Trace = TraceContext.Current };

    // One nudge per journal append: a watcher whose selection names none of the appended signals is not nudged.
    private protected Task Notify(Signal[] stamped, long sequence)
    {
        if (stamped.Length == 0) { return Task.CompletedTask; }
        return Deliver(new SignalNudge(Id, sequence), stamped);
    }

    private Task Deliver(SignalNudge nudge, IReadOnlyList<Signal> stamped)
    {
        var hub = Services.GetRequiredService<LocalSignalHub>();
        foreach (var signal in stamped) { hub.Publish(signal); }
        hub.Publish(GrainIdentity, nudge);
        return Observers.Notify(watch => watch.Observer.Nudge(nudge),
            watch => watch.Signals is null || stamped.Any(signal => watch.Signals.Contains(signal.GetType().Name)));
    }

    // The fanout of this activation: its own observers, under the same lease and selection rules as Watch.
    private sealed class Fanout(Neuron neuron) : ISignalFanout
    {
        public void Watch(NeuronAddress source, INeuronObserver observer, IReadOnlySet<string>? signals)
        {
            Own(source);
            neuron.Observers.Subscribe(observer, new(observer, signals?.ToHashSet(StringComparer.Ordinal)));
        }

        public void Unwatch(NeuronAddress source, INeuronObserver observer)
        {
            Own(source);
            neuron.Observers.Unsubscribe(observer);
        }

        public Task Nudge(NeuronAddress source, SignalNudge nudge, IReadOnlyList<Signal> appended)
        {
            Own(source);
            return neuron.Deliver(nudge, appended);
        }

        private void Own(NeuronAddress source)
        {
            if (source != GrainNeuronContext.AddressOf(neuron.GrainIdentity))
            { throw new ArgumentException($"An activation fans out only its own signals, not {source}.", nameof(source)); }
        }
    }

    // The signals a state change caused ride in its storage write when the storage takes them (a part log);
    // otherwise they are journaled after it. Either way a failed write publishes nothing.
    private protected async Task Announce(Func<Task> write, Signal[] signals)
        => await Announce(write, [.. signals.Select(signal => (signal, (Trace?)null))]);

    // Preserve each fact's causal trace while attaching the whole batch to its state write.
    private protected async Task Announce(Func<Task> write, (Signal Signal, Trace? Trace)[] signals)
    {
        // Stamping needs the grain's identity. A neuron constructed outside a silo has none: its write runs
        // first, so a storage failure surfaces as itself rather than as the missing identity.
        if (((IGrainBase)this).GrainContext is null && _context is null) { await write(); }
        var stamped = new Signal[signals.Length];
        for (var i = 0; i < signals.Length; i++)
        {
            using var entered = signals[i].Trace is { } trace ? TraceContext.Enter(trace) : null;
            stamped[i] = Stamp(signals[i].Signal);
        }
        bool taken;
        long sequence;
        using (SignalJournal.Attach(GrainIdentity, stamped))
        {
            await write();
            taken = SignalJournal.Taken;
            sequence = SignalJournal.LastSequence;
        }
        if (!taken) { sequence = await Journal.Append(GrainIdentity, stamped); }
        await Notify(stamped, sequence);
    }
}

// Storage hands back a non-null record before the first write: built with the state's parameterless
// constructor when it has one, uninitialized otherwise.
public abstract class Neuron<TState>(IPersistentState<TState> store) : Neuron where TState : class
{
    protected TState Snapshot => store.State;

    // Existence is the record's, not the object's.
    protected bool Exists => store.RecordExists;

    protected Task Save(TState next, Signal changed, Signal? acted = null) =>
        Announce(() => SaveQuietly(next), acted is null ? [changed] : [changed, acted]);

    protected Task Save(TState next, (Signal Signal, Trace? Trace)[] signals) =>
        Announce(() => SaveQuietly(next), signals);

    // Writes without announcing: for plumbing that no watcher observes. A neuron keeps all of its
    // persisted state in this one record, so its first write is the one that registers it.
    protected async Task SaveQuietly(TState next)
    {
        var previous = store.State;
        store.State = next;
        try { await store.WriteStateAsync(); }
        catch { store.State = previous; throw; }
        OnCommitted();
    }

    // Runs only after the write has succeeded, and before the caller of Save is resumed.
    protected virtual void OnCommitted() { }

    // Clearing the record is what ends the neuron's existence: the kernel retires the instance.
    protected Task Clear(Signal changed) => Announce(store.ClearStateAsync, [changed]);
}
