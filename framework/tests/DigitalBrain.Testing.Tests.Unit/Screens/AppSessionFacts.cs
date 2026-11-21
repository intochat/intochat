using DigitalBrain.Access;
using DigitalBrain.Examples.Calculator;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using Xunit;

namespace DigitalBrain.Testing.Tests.Unit.Screens;

[Signal("counter.increment")]
public sealed record CounterIncrement(int By = 1) : Signal;

public sealed record CounterState(int Count)
{
    public CounterState() : this(0) { }
}

[Neuron("test.counter")]
public sealed class CounterScreen : Screen<CounterState>
{
    protected override View Render(CounterState state) => new(
        "Counter",
        new Column([
            new Text($"Count: {state.Count}"),
            new Button("inc", "Increment", new Command(".", "counter.increment")),
        ]));

    [On]
    public Task Increment(CounterIncrement signal) => Show(State with { Count = State.Count + signal.By });
}

public sealed class AppSessionFacts
{
    [Fact]
    public async Task OpenAsync_and_Send_drive_CalculatorScreen_and_assert_text_and_generation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = TestOS.Local();
        os.Register(typeof(CalculatorScreen).Assembly);

        await using var session = await AppSession.OpenAsync<CalculatorScreen>(os, ct: ct);

        session
            .ShouldDisplayText("Calculator")
            .ShouldDisplayText("0")
            .ShouldNotDisplayText("12")
            .ShouldHaveGeneration(1);

        await session.Send(new CalculatorPress("7"), ct);
        session.ShouldDisplayText("7").ShouldHaveGeneration(2);

        await session.Send(new CalculatorPress("+"), ct);
        await session.Send(new CalculatorPress("5"), ct);
        await session.Send(new CalculatorPress("="), ct);

        session
            .ShouldDisplayText("12")
            .ShouldNotDisplayText("7")
            .ShouldHaveGeneration(4);
    }

    [Fact]
    public async Task Click_activates_button_and_updates_view_and_generation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = TestOS.Local();
        os.Register(typeof(CounterScreen).Assembly);
        var runtime = new TestOSRuntimeAdapter(os);

        await using var session = await AppSession.OpenAsync<CounterScreen>(runtime, ct: ct);

        session
            .ShouldDisplayText("Counter")
            .ShouldDisplayText("Count: 0")
            .ShouldHaveGeneration(1);

        await session.Click("inc", ct);
        session
            .ShouldDisplayText("Count: 1")
            .ShouldNotDisplayText("Count: 0")
            .ShouldHaveGeneration(2);

        await session.Click("inc", ct);
        session
            .ShouldDisplayText("Count: 2")
            .ShouldHaveGeneration(3);

        var refreshed = await session.Refresh(ct);
        Assert.Equal(3, refreshed.Generation);
    }

    private sealed class TestOSRuntimeAdapter(TestOS os) : INeuronRuntime
    {
        public NeuronRuntimeKind Kind => NeuronRuntimeKind.TestOS;
        public BrainId Brain => new("test");
        public IServiceProvider Services => throw new NotSupportedException();
        public EdgeAuthority Edge => os.Edge;
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
        public Task Idle() => os.Idle();
        public Task Elapse(TimeSpan by, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<T>> Signals<T>(NeuronId publisher, CancellationToken ct) where T : Signal => throw new NotSupportedException();
        public Task ApproveAsPerson(string planId, CancellationToken ct) => Task.CompletedTask;
        public Task Restart(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
