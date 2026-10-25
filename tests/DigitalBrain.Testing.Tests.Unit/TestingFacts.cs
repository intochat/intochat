using DigitalBrain.Spi;

namespace DigitalBrain.Testing.Tests.Unit;

public sealed record Pinged(int Count) : Signal;

public sealed record Unrelated : Signal;

public sealed class Counter
{
    public int Count { get; set; }
}

public interface IGreeter
{
    string Greet();
}

public sealed class Greeter : IGreeter
{
    public string Greet() => "hi";
}

[Neuron("sample")]
public sealed class SampleNeuron : Neuron<Counter>, IRemindedNeuron
{
    public List<string> Reminded { get; } = [];

    public DeactivationCause? Deactivated { get; private set; }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        var next = new Counter { Count = State.Count + 1 };
        await Save(next, new Pinged(next.Count));
    }

    protected override Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken)
    {
        Deactivated = cause;
        return Task.CompletedTask;
    }

    public Task OnReminder(string name, CancellationToken cancellationToken)
    {
        Reminded.Add(name);
        return Task.CompletedTask;
    }
}

public sealed class TestingFacts
{
    private static readonly NeuronId Id = new(new BrainId("alice"), "sample", "one");

    [Fact]
    public async Task Harness_attaches_a_fake_context_and_runs_activation()
    {
        var harness = await NeuronHarness.Create<SampleNeuron>(Id, TestContext.Current.CancellationToken);

        Assert.Equal(Id, harness.Context.Id);
        Assert.Equal(1, harness.AssertPublished<Pinged>(signal => signal.Count == 1).Count);
        Assert.Equal(1, harness.Context.MemoryState.Saves);
    }

    [Fact]
    public async Task AssertPublished_fails_with_the_recorded_list()
    {
        var harness = await NeuronHarness.Create<SampleNeuron>(Id, TestContext.Current.CancellationToken);

        var error = Assert.Throws<NeuronAssertionException>(() => harness.AssertPublished<Unrelated>());
        Assert.Contains("Pinged", error.Message, StringComparison.Ordinal);
        Assert.Throws<NeuronAssertionException>(() => harness.AssertPublished<Pinged>(signal => signal.Count == 5));
        Assert.Throws<NeuronAssertionException>(harness.AssertNothingPublished);
    }

    [Fact]
    public async Task Harness_runs_deactivation()
    {
        var harness = await NeuronHarness.Create<SampleNeuron>(Id, TestContext.Current.CancellationToken);
        await harness.Deactivate(DeactivationCause.Idle, TestContext.Current.CancellationToken);
        Assert.Equal(DeactivationCause.Idle, harness.Neuron.Deactivated);
    }

    [Fact]
    public async Task State_saves_loads_and_clears()
    {
        var state = new InMemoryNeuronState();
        await state.Save(new Counter { Count = 3 }, [new Pinged(3)]);
        Assert.Equal([new Pinged(3)], state.Signals);
        Assert.Equal(3, (await state.Load<Counter>())!.Count);
        await state.Clear();
        Assert.Null(await state.Load<Counter>());
        Assert.False(state.Exists);
    }

    [Fact]
    public async Task Reminders_fire_on_the_attached_neuron()
    {
        var harness = await NeuronHarness.Create<SampleNeuron>(Id, TestContext.Current.CancellationToken);
        await harness.Context.Reminders.Start("daily", TimeSpan.FromHours(1), TimeSpan.FromDays(1));

        await harness.Context.Reminders.Fire("daily", TestContext.Current.CancellationToken);

        Assert.Equal(["daily"], harness.Neuron.Reminded);
        Assert.Equal(["daily"], await harness.Context.Reminders.Names());
    }

    [Fact]
    public async Task Reminders_without_a_reminded_target_throw()
    {
        var reminders = new ManualReminders { Target = new object() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => reminders.Fire("x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Timers_fire_when_time_advances()
    {
        var time = new ManualTime();
        var start = time.GetUtcNow();
        var timers = new ManualTimers(time);
        var seen = new List<string>();
        timers.OnFire = name => { seen.Add(name); return Task.CompletedTask; };
        timers.Start("once", TimeSpan.FromSeconds(10), null);
        timers.Start("tick", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        Assert.Empty(await timers.Advance(TimeSpan.FromSeconds(4)));
        var fired = await timers.Advance(TimeSpan.FromSeconds(8));

        Assert.Equal(["tick", "once", "tick"], fired);
        Assert.Equal(fired, seen);
        Assert.Equal(start + TimeSpan.FromSeconds(12), time.GetUtcNow());
        Assert.Equal(["tick"], timers.Names);
        timers.Stop("tick");
        Assert.Empty(await timers.Advance(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Proxies_return_registered_instances()
    {
        var other = new NeuronId(new BrainId("alice"), "greeter", "g");
        var proxies = new FakeNeuronProxies().Register<IGreeter>(other, new Greeter());

        Assert.Equal("hi", proxies.Get<IGreeter>(other).Greet());
        var missing = new NeuronId(new BrainId("alice"), "greeter", "nobody");
        var error = Assert.Throws<KeyNotFoundException>(() => proxies.Get<IGreeter>(missing));
        Assert.Contains(missing.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_broker_returns_registered_capabilities()
    {
        var broker = new FakeCapabilityBroker().Register<IGreeter>(new Greeter());
        Assert.Equal("hi", (await broker.Resolve<IGreeter>(new BrainId("alice"), TestContext.Current.CancellationToken)).Greet());
    }

    [Fact]
    public async Task Synapses_and_operations_are_recorded()
    {
        var synapses = new RecordingSynapses();
        var to = new NeuronId(new BrainId("alice"), "sink", "s");
        await synapses.Connect(new Synapse(Id, typeof(Pinged), to));
        Assert.Single(await synapses.Of(Id));

        var operations = new FakeOperations();
        var op = await operations.Begin("import", TestContext.Current.CancellationToken);
        await operations.Complete(op, succeeded: true, TestContext.Current.CancellationToken);
        Assert.True(operations.All[op].Succeeded);
    }

    [Fact]
    public async Task SiloHost_without_the_runtime_names_the_package()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SiloHost.Local(ct: TestContext.Current.CancellationToken));
        Assert.Contains("DigitalBrain.Runtime.Orleans", error.Message, StringComparison.Ordinal);
    }
}
