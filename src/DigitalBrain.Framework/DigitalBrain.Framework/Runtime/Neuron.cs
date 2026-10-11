using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Utilities;

namespace DigitalBrain.Framework;

// Every neuron is observable and activated through IActivationHandler<T>; OnActivateAsync is
// sealed so the runtime owns the order and the failure handling.
public abstract class Neuron : Grain, INeuron
{
    private readonly Guid _activation = Guid.NewGuid();
    private sealed record WatchRegistration(INeuronObserver Observer, HashSet<string>? Signals);
    private ObserverManager<INeuronObserver, WatchRegistration>? _observers;
    private ObserverManager<INeuronObserver, WatchRegistration> Observers => _observers ??= new(
        ServiceProvider.GetRequiredService<IOptions<ObserverOptions>>().Value.Lease,
        ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Neuron.Observers"));

    protected TimeSpan ObserverRenewal => TimeSpan.FromTicks(ServiceProvider.GetRequiredService<IOptions<ObserverOptions>>().Value.Lease.Ticks / 5);

    // Transitional: the full grain id until keys are brain-owned.
    public NeuronId Id => new(this.GetGrainId().ToString());

    public sealed override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        if (ServiceProvider.GetService<IStandInActivations>()?.IsStandIn(this.GetGrainId()) == true) { return; }
        var handlers = ServiceProvider.GetServices(typeof(IActivationHandler<>).MakeGenericType(GetType()));
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ((IActivationHandler)handler!).Activated(this, cancellationToken);
        }
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

    // A signal is durable before any observer sees it (Q27).
    protected async Task PublishAsync(Signal signal)
    {
        var stamped = Stamp(signal);
        await Journal.Append(this.GetGrainId(), [stamped]);
        await Notify(stamped);
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
        await Journal.Append(this.GetGrainId(), stamped);
        foreach (var signal in stamped) { await Notify(signal); }
    }

    private protected ISignalJournal Journal => ServiceProvider.GetRequiredService<ISignalJournal>();

    private protected Signal Stamp(Signal signal) => signal with { Publisher = Id, Trace = TraceContext.Current };

    private protected Task Notify(Signal stamped)
    {
        ServiceProvider.GetRequiredService<LocalSignalHub>().Publish(this.GetGrainId(), stamped);
        return Observers.Notify(watch => watch.Observer.OnSignalAsync(stamped),
            watch => watch.Signals is null || watch.Signals.Contains(stamped.GetType().Name));
    }

    // The signals a state change caused ride in its storage write when the storage takes them (a part log);
    // otherwise they are journaled after it. Either way a failed write publishes nothing.
    private protected async Task Announce(Func<Task> write, Signal[] signals)
    {
        // Stamping needs the grain's identity. A neuron constructed outside a silo has none: its write runs
        // first, so a storage failure surfaces as itself rather than as the missing identity.
        if (((IGrainBase)this).GrainContext is null) { await write(); }
        Signal[] stamped = [.. signals.Select(Stamp)];
        bool taken;
        using (SignalJournal.Attach(this.GetGrainId(), stamped))
        {
            await write();
            taken = SignalJournal.Taken;
        }
        if (!taken) { await Journal.Append(this.GetGrainId(), stamped); }
        foreach (var signal in stamped) { await Notify(signal); }
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

    // Writes without announcing: for plumbing that no watcher observes. A neuron keeps all of its
    // persisted state in this one record, so its first write is the one that registers it.
    protected async Task SaveQuietly(TState next)
    {
        var previous = store.State;
        store.State = next;
        try { await store.WriteStateAsync(); }
        catch { store.State = previous; throw; }
    }

    // Clearing the record is what ends the neuron's existence: the kernel retires the instance.
    protected Task Clear(Signal changed) => Announce(store.ClearStateAsync, [changed]);
}
