using DigitalBrain.Invocation;
using DigitalBrain.Spi;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>What the host runtime knows about the current call, beyond <see cref="INeuronContext"/>.</summary>
internal interface IHostedNeuronContext
{
    /// <summary>The calling neuron, written <c>brain/type/key</c>, or empty for a caller outside any neuron. Read from RequestContext.</summary>
    string Caller { get; }

    /// <summary>The silo running this activation.</summary>
    string Silo { get; }
}

/// <summary>
/// The minimal context of a hosted neuron: id, services, clock, typed proxies, caller and deferred work.
/// State, journal, timers, reminders, operations and synapses arrive with the full runtime (04-11) and refuse until then.
/// </summary>
internal sealed class HostedNeuronContext(NeuronId id, IServiceProvider services, IGrainFactory grains, NeuronTypeRegistry types, string silo, Action<Func<Task>> defer)
    : INeuronContext, IHostedNeuronContext
{
    internal const string CallerKey = "db.caller";

    public NeuronId Id => id;

    public IServiceProvider Services => services;

    public TimeProvider Time => (TimeProvider?)services.GetService(typeof(TimeProvider)) ?? TimeProvider.System;

    public INeuronState State => throw Later(nameof(State));

    public ITimers Timers => throw Later(nameof(Timers));

    public IReminders Reminders => throw Later(nameof(Reminders));

    public IOperations Operations => throw Later(nameof(Operations));

    public ISynapses Synapses => throw Later(nameof(Synapses));

    public ISignalHistory History => throw Later(nameof(History));

    public string Caller => RequestContext.Get(CallerKey) as string ?? string.Empty;

    public string Silo => silo;

    public Task Publish(Signal signal) => throw Later(nameof(Publish));

    public T Get<T>(NeuronId target) where T : class
    {
        if (target.Brain != id.Brain) { throw new ArgumentException($"Neuron {target} belongs to another brain than {id}.", nameof(target)); }
        return NeuronProxy.Create<T>(target, new OrleansInvocationSink(grains, types, id));
    }

    public void Defer(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        defer(work);
    }

    private static NotSupportedException Later(string member)
        => new($"INeuronContext.{member} is not available on the host grain yet; it arrives with the full runtime registration.");
}
