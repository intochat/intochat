using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Examples.Calculator;
using DigitalBrain.Identity.Local;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Calculator.Tests.Integration;

public sealed record InteractiveState
{
    public int Count { get; init; }
}

[Signal("interactive.increment")]
public sealed record InteractiveIncrement(int Amount = 1) : Signal;

[Neuron("app.interactive-adversarial")]
public sealed class InteractiveAdversarialScreen : Screen<InteractiveState>
{
    protected override View Render(InteractiveState state)
        => new("Interactive", new Column([
            new Heading($"Count: {state.Count}"),
            new Button("btn-inc", "Increment", Sends(new InteractiveIncrement(1)))
        ]));

    [On]
    public Task When(InteractiveIncrement signal) => Show(State with { Count = State.Count + signal.Amount });
}

public sealed class AdversarialRuntimeFixture : BothRuntimesFixture
{
    private readonly TempDirectory _home = new("db-adv-", create: false);

    protected override IReadOnlyList<Type> Modules => [typeof(CalculatorScreen), typeof(InteractiveAdversarialScreen)];

    protected override void ConfigureSilo(IServiceCollection services)
        => services.AddLocalIdentity(SiloNeurons.Get, _home.Path);

    protected override ValueTask DisposeCoreAsync()
    {
        _home.Dispose();
        return base.DisposeCoreAsync();
    }
}

public sealed class CalculatorAdversarialFacts(AdversarialRuntimeFixture fixture) : IClassFixture<AdversarialRuntimeFixture>
{
    // =========================================================================
    // 1. Concurrency & Serial Turn Execution via Orleans Mailbox
    // =========================================================================

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ConcurrentRequests_FiftyPressesSerializedWithoutRaceCondition(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("person", "person-account", runtime.Brain));
        var screenId = runtime.Id("app.calculator", "fifty-presses-" + Guid.NewGuid().ToString("N"));
        var screen = runtime.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1, initialView.Generation);

        // Fire 50 concurrent Request<CalculatorPress>("1") calls across threads
        const int count = 50;
        var tasks = Enumerable.Range(0, count)
            .Select(_ => Task.Run(() => screen.Request<CalculatorPress>(new CalculatorPress("1"))))
            .ToArray();

        await Task.WhenAll(tasks);
        await runtime.Idle();

        var finalView = await screen.Read();
        Assert.NotNull(finalView);

        // Each press appends '1' to the expression, so 50 presses must result in exactly 50 '1's:
        var rawText = finalView.View.GetRawText();
        var expectedExpression = new string('1', count);
        Assert.Contains(expectedExpression, rawText);

        // Generation must have incremented exactly 50 times from 1 to 51:
        Assert.Equal(1 + count, finalView.Generation);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ConcurrentRequests_OneHundredPressesSerializedWithoutRaceCondition(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("person", "person-account", runtime.Brain));
        var screenId = runtime.Id("app.calculator", "hundred-presses-" + Guid.NewGuid().ToString("N"));
        var screen = runtime.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1, initialView.Generation);

        // Fire 100 concurrent Request<CalculatorPress>("2") calls across threads
        const int count = 100;
        using var barrier = new Barrier(count);
        var tasks = Enumerable.Range(0, count)
            .Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait(TestContext.Current.CancellationToken);
                await screen.Request<CalculatorPress>(new CalculatorPress("2"));
            }, TestContext.Current.CancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
        await runtime.Idle();

        var finalView = await screen.Read();
        Assert.NotNull(finalView);

        // Each press appends '2' to the expression, so 100 presses must result in exactly 100 '2's:
        var rawText = finalView.View.GetRawText();
        var expectedExpression = new string('2', count);
        Assert.Contains(expectedExpression, rawText);

        // Generation must have incremented exactly 100 times from 1 to 101:
        Assert.Equal(1 + count, finalView.Generation);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ConcurrentCommands_FromOneView_AreEachHandledOnce(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("person", "person-account", runtime.Brain));
        var screenId = runtime.Id("app.interactive-adversarial", "command-race-" + Guid.NewGuid().ToString("N"));
        var screen = runtime.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1, initialView.Generation);
        Assert.Contains("Count: 0", initialView.View.GetRawText());

        // All 20 callers send the button's command taken from the SAME initial view. A command carries no generation: each is
        // judged by the state its turn sees, and the turns run one at a time, so every one is handled and none is lost.
        const int concurrentCallers = 20;
        var command = initialView.CommandAt("btn-inc");
        var failures = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, concurrentCallers).Select(_ => Task.Run(async () =>
        {
            try { await screen.Dispatch(command); }
            catch (Exception ex) { failures.Add(ex); }
        })).ToArray();

        await Task.WhenAll(tasks);
        await runtime.Idle();

        Assert.Empty(failures);
        var finalView = await screen.Read();
        Assert.NotNull(finalView);
        Assert.Equal(1 + concurrentCallers, finalView.Generation);
        Assert.Contains($"Count: {concurrentCallers}", finalView.View.GetRawText());
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ChainedSequentialCommands_IncrementGenerationPredictably(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("person", "person-account", runtime.Brain));
        var screenId = runtime.Id("app.interactive-adversarial", "chained-activations-" + Guid.NewGuid().ToString("N"));
        var screen = runtime.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(1, view.Generation);

        // Perform 5 properly sequenced activations: Count goes 0 -> 1 -> 2 -> 3 -> 4 -> 5
        const int steps = 5;
        for (var i = 0; i < steps; i++)
        {
            await screen.Click("btn-inc");
            view = await screen.Read();
            Assert.NotNull(view);
            Assert.Equal(2 + i, view.Generation);
            Assert.Contains($"Count: {i + 1}", view.View.GetRawText());
        }
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task Durability_StateAndGenerationSurvive(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.Edge.Enter(runtime.Edge.Person("person", "person-account", runtime.Brain));
        var screenId = runtime.Id("app.interactive-adversarial", "durability-" + Guid.NewGuid().ToString("N"));
        var screen = runtime.Get<IScreen>(screenId);

        var view0 = await screen.Read();
        Assert.NotNull(view0);
        Assert.Equal(1, view0.Generation);
        Assert.Contains("Count: 0", view0.View.GetRawText());

        // Perform 3 sequential activations
        await screen.Click("btn-inc");
        var view1 = await screen.Read();
        Assert.NotNull(view1);
        Assert.Equal(2, view1.Generation);
        Assert.Contains("Count: 1", view1.View.GetRawText());

        await screen.Click("btn-inc");
        var view2 = await screen.Read();
        Assert.NotNull(view2);
        Assert.Equal(3, view2.Generation);
        Assert.Contains("Count: 2", view2.View.GetRawText());

        await screen.Click("btn-inc");
        var view3 = await screen.Read();
        Assert.NotNull(view3);
        Assert.Equal(4, view3.Generation);
        Assert.Contains("Count: 3", view3.View.GetRawText());

        // Read again through fresh proxy lookup to verify state & view generation persistence
        var reloadedScreen = runtime.Get<IScreen>(screenId);
        var reloadedView = await reloadedScreen.Read();
        Assert.NotNull(reloadedView);
        Assert.Equal(4, reloadedView.Generation);
        Assert.Contains("Count: 3", reloadedView.View.GetRawText());
    }
}
