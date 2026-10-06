using DigitalBrain.Framework.Enforcement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Utilities;

namespace DigitalBrain.Framework;

/// <summary>
/// The grain every neuron is: observable, and activated through the activation pipeline.
/// </summary>
/// <remarks>
/// Activation is sealed. A module that needs work on activation registers an
/// <see cref="IActivationHandler{TNeuron}"/> for its neuron type instead of overriding the grain
/// lifecycle, so the runtime owns the order and the failure handling.
/// </remarks>
public abstract class Neuron : Grain, INeuron
{
    public virtual NeuronAccess Access(string operation) => NeuronAccess.ForBrainKey(this.GetPrimaryKeyString());

    private readonly Guid _activation = Guid.NewGuid();
    private ObserverManager<INeuronObserver>? _observers;
    private ObserverManager<INeuronObserver> Observers => _observers ??= new(
        ServiceProvider.GetRequiredService<IOptions<ObserverOptions>>().Value.Lease,
        ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Neuron.Observers"));

    protected TimeSpan ObserverRenewal => TimeSpan.FromTicks(ServiceProvider.GetRequiredService<IOptions<ObserverOptions>>().Value.Lease.Ticks / 5);

    /// <summary>This neuron's id as stamped on its signals. Transitional: the full grain id until keys are brain-owned.</summary>
    public NeuronId Id => new(this.GetGrainId().ToString());

    public sealed override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        var handlers = ServiceProvider.GetServices(typeof(IActivationHandler<>).MakeGenericType(GetType()));
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ((IActivationHandler)handler!).Activated(this, cancellationToken);
        }
    }

    public virtual Task<Guid> Watch(INeuronObserver observer)
    {
        Observers.Subscribe(observer, observer);
        return Task.FromResult(_activation);
    }

    public Task Unwatch(INeuronObserver observer)
    {
        Observers.Unsubscribe(observer);
        return Task.CompletedTask;
    }

    protected Task PublishAsync(Signal signal)
    {
        var stamped = signal with { Publisher = Id };
        ServiceProvider.GetRequiredService<LocalSignalHub>().Publish(this.GetGrainId(), stamped);
        return Observers.Notify(observer => observer.OnSignalAsync(stamped));
    }
}

public abstract class Neuron<TState>(IPersistentState<TState> store) : Neuron where TState : class, new()
{
    protected TState Snapshot
    {
        get
        {
            store.State ??= new TState();
            return store.State;
        }
    }

    protected async Task Save(TState next, Signal changed, Signal? acted = null)
    {
        var previous = store.State;
        store.State = next;
        try
        {
            await store.WriteStateAsync();
        }
        catch
        {
            store.State = previous;
            throw;
        }
        await PublishAsync(changed);
        if (acted is not null)
        {
            await PublishAsync(acted);
        }
    }
}
