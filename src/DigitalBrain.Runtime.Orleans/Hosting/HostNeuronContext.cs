using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Synapses;
using DigitalBrain.Spi;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>What the host runtime knows about the current call, beyond <see cref="INeuronContext"/>.</summary>
internal interface IHostNeuronContext
{
    /// <summary>The calling neuron, written <c>brain/type/key</c>, or empty for a caller outside any neuron. Read from RequestContext.</summary>
    string Caller { get; }

    /// <summary>The silo running this activation.</summary>
    string Silo { get; }
}

/// <summary>
/// The context of a hosted neuron: id, services, clock, state over BrainLog with publish after append, timers and reminders as
/// host grain turns, the brain's signal history, synapses over the activation's <see cref="SynapseEngine"/>, typed proxies, caller
/// provenance and deferred work. Operations are not hosted yet.
/// </summary>
internal sealed class HostNeuronContext : INeuronContext, IHostNeuronContext
{
    internal const string CallerKey = "db.caller";

    private readonly IServiceProvider _services;
    private readonly IGrainFactory _grains;
    private readonly NeuronTypeRegistry _types;
    private readonly Action<Func<Task>> _defer;
    private readonly HostNeuronState _state;
    private readonly HostTimers _timers;

    public HostNeuronContext(NeuronId id, GrainId grainId, NeuronRegistration registration, IServiceProvider services, IGrainFactory grains,
        NeuronTypeRegistry types, string silo, IHostTicks ticks, Action<Func<Task>> defer, SynapseEngine synapses)
    {
        Synapses = new HostSynapses(synapses);
        Id = id;
        Silo = silo;
        _services = services;
        _grains = grains;
        _types = types;
        _defer = defer;
        _state = new HostNeuronState(id, grainId, registration, services);
        _timers = new HostTimers(ticks, registration);
    }

    public NeuronId Id { get; }

    public IServiceProvider Services => _services;

    public TimeProvider Time => (TimeProvider?)_services.GetService(typeof(TimeProvider)) ?? TimeProvider.System;

    public INeuronState State => _state;

    internal HostNeuronState HostState => _state;

    public ITimers Timers => _timers;

    public IReminders Reminders => _timers;

    public IOperations Operations => throw new NotSupportedException("INeuronContext.Operations is not hosted on the Orleans runtime yet.");

    public ISynapses Synapses { get; }

    /// <summary>The signal history of this neuron's brain only (T-04-35).</summary>
    /// <exception cref="InvalidOperationException">The <c>cluster</c> store is not BrainLog.</exception>
    public ISignalHistory History => (_services.GetService<BrainSignalHistories>()
        ?? throw new InvalidOperationException("Signal history needs BrainLog as the cluster store (BrainStorageRegistration).")).For(Id.Brain);

    public string Caller => RequestContext.Get(CallerKey) as string ?? string.Empty;

    public string Silo { get; }

    public Task Publish(Signal signal) => _state.Publish(signal);

    public T Get<T>(NeuronId target) where T : class
    {
        if (target.Brain != Id.Brain) { throw new ArgumentException($"Neuron {target} belongs to another brain than {Id}.", nameof(target)); }
        return NeuronProxy.Create<T>(target, new OrleansInvocationSink(_grains, _types, Id));
    }

    public void Defer(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _defer(work);
    }
}
