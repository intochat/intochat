using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Unit;

// The Gmail neuron is plain (06-08): a save that fails leaves its state as it was, and the next save starts from that state.
public sealed class GmailStateFacts
{
    [Fact]
    public async Task FailedAggregateWritePreservesThePreviousSnapshotOnTheSameActivation()
    {
        var context = new FaultingContext(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("facts"), "gmail", "connection/gmail/c1"),
            new GmailState { GrantGeneration = 7, BoundMailbox = "fixture@example.test" });
        var neuron = new GmailNeuron();
        NeuronAttachment.Attach(neuron, context);
        typeof(DigitalBrain.Neurons.Neuron).GetField("_seat", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(neuron, context.Inner);
        await NeuronAttachment.Activate(neuron, CancellationToken.None);
        var initial = Assert.IsType<GmailState>(context.Stored);
        context.Fail = true;

        await Assert.ThrowsAsync<IOException>(() => neuron.SaveGmailStateAsync(next => next.GrantGeneration = 8));
        Assert.Same(initial, context.Stored);
        Assert.Equal(7, initial.GrantGeneration);

        context.Fail = false;
        await neuron.SaveGmailStateAsync(next => next.GrantGeneration = 9);
        var stored = Assert.IsType<GmailState>(context.Stored);
        Assert.NotSame(initial, stored);
        Assert.Equal(7, initial.GrantGeneration);
        Assert.Equal(9, stored.GrantGeneration);
        Assert.Equal("fixture@example.test", stored.BoundMailbox);
    }

    // A fake context whose state store can be made to fail; everything else is the testing kit's fake.
    private sealed class FaultingContext : INeuronContext, INeuronState
    {
        private readonly FakeNeuronContext _inner;
        private object? _previous;

        public FaultingContext(DigitalBrain.NeuronId id, GmailState initial)
        {
            _inner = new(id, new ServiceCollection()
                .AddSingleton(TimeProvider.System).BuildServiceProvider());
            _previous = initial;
            _inner.MemoryState.Save(initial, []).GetAwaiter().GetResult();

            var savedField = typeof(InMemoryNeuronState).GetField("_saved", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var valueField = typeof(InMemoryNeuronState).GetField("_value", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            savedField?.SetValue(_inner.MemoryState, (Action<IReadOnlyList<DigitalBrain.Signal>>)(_ =>
            {
                if (Fail)
                {
                    valueField?.SetValue(_inner.MemoryState, _previous);
                    throw new IOException("Fixture storage unavailable.");
                }
                _previous = Stored;
            }));
        }

        public FakeNeuronContext Inner => _inner;
        public bool Fail { get; set; }
        public object? Stored => typeof(InMemoryNeuronState).GetField("_value", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(_inner.MemoryState);

        public DigitalBrain.NeuronId Id => _inner.Id;
        public Caller? Caller => ((INeuronContext)_inner).Caller;
        public IServiceProvider Services => _inner.Services;
        public TimeProvider Time => _inner.Time;
        public INeuronState State => this;
        public IOperationsQuery OperationsQuery => throw new NotSupportedException();
        public ITimers Timers => _inner.Timers;
        public IReminders Reminders => _inner.Reminders;
        public IOperations Operations => null!;
        public ISynapses Synapses => _inner.Synapses;
        public ISignalHistory History => null!;
        public IConfirmations Confirmations => null!;
        public Task Publish(DigitalBrain.Signal signal) => _inner.Publish(signal);
        public T Get<T>(DigitalBrain.NeuronId target) where T : class => _inner.Get<T>(target);
        public void Defer(Func<Task> work) => _inner.Defer(work);

        public Task<T?> Load<T>() => Task.FromResult(Stored is T value ? value : default);

        public Task Save<T>(T next, IReadOnlyList<DigitalBrain.Signal> signals) => _inner.MemoryState.Save(next, signals);

        public Task Clear() => _inner.MemoryState.Clear();
    }

    private interface INeuronState
    {
        Task<T?> Load<T>();
        Task Save<T>(T next, IReadOnlyList<DigitalBrain.Signal> signals);
        Task Clear();
    }
}
