using System.Reflection;
using DigitalBrain.Spi;

namespace DigitalBrain.Tests.Unit.Neurons;

public sealed record CounterState(int Count)
{
    public CounterState() : this(0) { }
}

public sealed record Counted(int Count) : Signal;

public sealed record Noticed(string What) : Signal;

public interface IOther
{
    string Name { get; }
}

public sealed class Other : IOther
{
    public string Name => "other";
}

[Neuron("counter")]
public sealed class CounterNeuron : Neuron<CounterState>
{
    public int Count => State.Count;

    public int SeenAtActivation { get; private set; } = -1;

    public Task Increment()
    {
        var next = State with { Count = State.Count + 1 };
        return Save(next, new Counted(next.Count));
    }

    public Task IncrementTwice()
    {
        var next = State with { Count = State.Count + 2 };
        return Save(next, [new Counted(next.Count), new Noticed("twice")]);
    }

    public IOther Reach(NeuronId id) => Get<IOther>(id);

    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        SeenAtActivation = State.Count;
        return Task.CompletedTask;
    }
}

public sealed class Notifier : Neuron
{
    public Task Notice() => Publish(new Noticed("hello"));
}

public sealed record Ledger(long Revision, IReadOnlyCollection<string> Operations, int Balance) : IRevisioned<Ledger>
{
    public Ledger() : this(0, [], 0) { }

    public Ledger Advanced(string operationId) => this with { Revision = Revision + 1, Operations = [.. Operations, operationId] };
}

public sealed record Credited(int Balance) : Signal;

public sealed class LedgerNeuron : RevisionedNeuron<Ledger>
{
    public long Current => Revision;

    public Task<bool> Credit(string operation, long expected, int amount)
        => Commit(operation, expected, state => state with { Balance = state.Balance + amount }, state => new Credited(state.Balance));
}

public sealed class PlainNeuronFacts
{
    private static readonly BrainId Brain = new("alice");
    private static readonly NeuronId CounterId = new(Brain, "counter", "c");

    private static (TNeuron Neuron, RecordingContext Context) Attached<TNeuron>() where TNeuron : Neuron, new()
    {
        var neuron = new TNeuron();
        var context = new RecordingContext(CounterId);
        NeuronAttachment.Attach(neuron, context);
        return (neuron, context);
    }

    [Fact]
    public async Task Save_next_changed_writes_state_with_exactly_one_signal()
    {
        var (neuron, context) = Attached<CounterNeuron>();

        await neuron.Increment();

        var write = Assert.Single(context.Writes);
        Assert.Equal(new CounterState(1), write.State);
        Assert.Equal([new Counted(1)], write.Signals);
        Assert.Equal(1, neuron.Count);
        Assert.Empty(context.Published);
    }

    [Fact]
    public async Task Save_next_signals_is_one_write_with_signals_in_order_and_there_is_no_bare_save()
    {
        var (neuron, context) = Attached<CounterNeuron>();

        await neuron.IncrementTwice();

        var write = Assert.Single(context.Writes);
        Assert.Equal([new Counted(2), new Noticed("twice")], write.Signals);
        var saves = typeof(Neuron<CounterState>).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Where(m => m.Name == "Save").ToList();
        Assert.Equal(2, saves.Count);
        Assert.All(saves, save => Assert.True(save.GetParameters().Length >= 2));
    }

    [Fact]
    public async Task Publish_on_a_stateless_neuron_writes_no_state()
    {
        var (neuron, context) = Attached<Notifier>();

        await neuron.Notice();

        Assert.Equal([new Noticed("hello")], context.Published);
        Assert.Empty(context.Writes);
    }

    [Fact]
    public async Task Revisioned_neuron_advances_and_refuses_a_stale_revision()
    {
        var (neuron, context) = Attached<LedgerNeuron>();

        Assert.True(await neuron.Credit("op-1", 0, 5));
        Assert.True(await neuron.Credit("op-2", 1, 5));
        Assert.Equal(2, neuron.Current);
        Assert.False(await neuron.Credit("op-2", 2, 5));

        var conflict = await Assert.ThrowsAsync<RevisionConflictException>(() => neuron.Credit("op-3", 1, 5));
        Assert.Equal(2, conflict.CurrentRevision);
        Assert.Equal(2, context.Writes.Count);
    }

    [Fact]
    public void Get_returns_the_context_answer_and_refuses_another_brain_first()
    {
        var (neuron, context) = Attached<CounterNeuron>();
        var other = new NeuronId(Brain, "other", "k");
        var instance = new Other();
        context.Neurons[other] = instance;

        Assert.Same(instance, neuron.Reach(other));

        var foreign = new NeuronId(new BrainId("bob"), "other", "k");
        Assert.Throws<ArgumentException>(() => neuron.Reach(foreign));
        Assert.Equal([other], context.Gets);
    }

    [Fact]
    public void Inherited_scheduling_attributes_and_neuron_state_name()
    {
        Assert.True(typeof(InterleavesWhenAttribute).GetCustomAttribute<AttributeUsageAttribute>()!.Inherited);
        Assert.True(typeof(ReentrantAttribute).GetCustomAttribute<AttributeUsageAttribute>()!.Inherited);
        Assert.Equal(AttributeTargets.Class, typeof(StatelessNeuronAttribute).GetCustomAttribute<AttributeUsageAttribute>()!.ValidOn);
        Assert.Null(new NeuronAttribute("counter").State);
        Assert.Equal("brain", new NeuronAttribute("counter") { State = "brain" }.State);
    }

    [Fact]
    public async Task State_is_loaded_before_activation_and_has_no_public_setter()
    {
        var (neuron, context) = Attached<CounterNeuron>();
        context.Store(new CounterState(7));

        await NeuronAttachment.Activate(neuron, TestContext.Current.CancellationToken);

        Assert.Equal(7, neuron.SeenAtActivation);
        var state = typeof(Neuron<CounterState>).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False(state.SetMethod!.IsPublic || state.SetMethod.IsFamily);
    }
}
