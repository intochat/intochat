using DigitalBrain.Spi;

namespace DigitalBrain.Tests.Unit.Neurons;

// Records what a neuron asks of its runtime: state writes, publishes and Get calls.
internal sealed class RecordingContext(NeuronId id) : INeuronContext, INeuronState
{
    private object? _stored;

    public List<(object State, IReadOnlyList<Signal> Signals)> Writes { get; } = [];

    public List<Signal> Published { get; } = [];

    public List<NeuronId> Gets { get; } = [];

    public Dictionary<NeuronId, object> Neurons { get; } = [];

    public List<Func<Task>> Deferred { get; } = [];

    public NeuronId Id { get; } = id;

    public IServiceProvider Services => throw new NotSupportedException();

    public TimeProvider Time => TimeProvider.System;

    public INeuronState State => this;

    public ITimers Timers => throw new NotSupportedException();

    public IReminders Reminders => throw new NotSupportedException();

    public IOperations Operations => throw new NotSupportedException();

    public ISynapses Synapses => throw new NotSupportedException();

    public void Store(object state) => _stored = state;

    public Task Publish(Signal signal)
    {
        Published.Add(signal);
        return Task.CompletedTask;
    }

    public T Get<T>(NeuronId id) where T : class
    {
        Gets.Add(id);
        return (T)Neurons[id];
    }

    public void Defer(Func<Task> work) => Deferred.Add(work);

    public Task<T?> Load<T>() => Task.FromResult(_stored is T value ? value : default);

    public Task Save<T>(T next, IReadOnlyList<Signal> signals)
    {
        Writes.Add((next!, [.. signals]));
        _stored = next;
        return Task.CompletedTask;
    }

    public Task Clear()
    {
        _stored = null;
        return Task.CompletedTask;
    }
}
