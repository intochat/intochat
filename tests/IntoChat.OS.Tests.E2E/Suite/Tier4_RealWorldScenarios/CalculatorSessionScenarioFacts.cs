using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier4_RealWorldScenarios;

public sealed record CalculatorScenarioState(
    string Display = "0",
    double? Accumulator = null,
    string? PendingOperator = null,
    bool ResetNext = false,
    string? LastCaller = null,
    int TurnCount = 0)
{
    public CalculatorScenarioState() : this("0", null, null, false, null, 0) { }

    public CalculatorScenarioState ApplyKey(string key, string? caller)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return this;
        }
        var token = key.Trim();

        if (token is "C" or "c")
        {
            return new CalculatorScenarioState("0", null, null, false, caller, TurnCount + 1);
        }

        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            var nextDisplay = ResetNext || Display == "0" ? token : Display + token;
            if (nextDisplay == Display && !ResetNext)
            {
                return this;
            }
            return this with { Display = nextDisplay, ResetNext = false, LastCaller = caller, TurnCount = TurnCount + 1 };
        }

        if (token is "+" or "-" or "*" or "/")
        {
            var current = double.TryParse(Display, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d;
            var value = PendingOperator is not null && Accumulator is { } acc && !ResetNext
                ? Evaluate(acc, current, PendingOperator)
                : current;
            return new CalculatorScenarioState(
                Format(value),
                value,
                token,
                ResetNext: true,
                LastCaller: caller,
                TurnCount: TurnCount + 1);
        }

        if (token == "=" && PendingOperator is { } op && Accumulator is { } left)
        {
            var right = double.TryParse(Display, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d;
            var result = Evaluate(left, right, op);
            return new CalculatorScenarioState(Format(result), null, null, ResetNext: true, LastCaller: caller, TurnCount: TurnCount + 1);
        }

        return this with { LastCaller = caller, TurnCount = TurnCount + 1 };
    }

    internal (string? Operator, double Divisor) PendingDivision(string key)
    {
        var token = key.Trim();
        var evaluates = (token is "=" or "+" or "-" or "*" or "/")
            && PendingOperator is not null && Accumulator is not null && (token == "=" || !ResetNext);
        return evaluates && double.TryParse(Display, NumberStyles.Float, CultureInfo.InvariantCulture, out var divisor)
            ? (PendingOperator, divisor)
            : (null, 1d);
    }

    private static double Evaluate(double left, double right, string op) => op switch
    {
        "+" => left + right,
        "-" => left - right,
        "*" => left * right,
        "/" => right == 0 ? throw new DivideByZeroException("Cannot divide by zero.") : left / right,
        _ => right,
    };

    private static string Format(double value) => value.ToString("G15", CultureInfo.InvariantCulture);
}

[Signal("calculator.key")]
public sealed record CalculatorKeySignal(string Key, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("calculator.clear")]
public sealed record CalculatorClearSignal([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

public sealed record DivisorNotZeroRule(string? PendingOperator, double Divisor) : Rule("divisor.not-zero")
{
    public override bool Broken => PendingOperator == "/" && Math.Abs(Divisor) < 0.0000001;
    public override string Message => "Cannot divide by zero.";
}

/*
# InteractiveCalculator
Interactive calculator screen performing multi-step arithmetic.
*/
[Neuron("app.interactive_calculator")]
public sealed class InteractiveCalculatorScreen : Screen<CalculatorScenarioState>
{
    protected override View Render(CalculatorScenarioState state)
    {
        var elements = new List<Node>
        {
            new Text($"Display: {state.Display}"),
            new Text($"Accumulator: {state.Accumulator?.ToString(CultureInfo.InvariantCulture) ?? "none"}"),
            new Text($"PendingOp: {state.PendingOperator ?? "none"}"),
            new Text($"LastCaller: {state.LastCaller ?? "none"}"),
            new Text($"TurnCount: {state.TurnCount}"),
            new Button("btn-clear", "C", Emits(new CalculatorClearSignal())),
            new Button("btn-equals", "=", Emits(new CalculatorKeySignal("="))),
            new Button("btn-add", "+", Emits(new CalculatorKeySignal("+"))),
            new Button("btn-sub", "-", Emits(new CalculatorKeySignal("-"))),
            new Button("btn-mul", "*", Emits(new CalculatorKeySignal("*"))),
            new Button("btn-div", "/", Emits(new CalculatorKeySignal("/")))
        };

        for (int i = 0; i <= 9; i++)
        {
            elements.Add(new Button($"btn-{i}", $"{i}", Emits(new CalculatorKeySignal($"{i}"))));
        }

        return new View("InteractiveCalculator", new Column([.. elements]));
    }

    [On]
    public async Task OnKey(CalculatorKeySignal signal)
    {
        var (pendingOp, divisor) = State.PendingDivision(signal.Key);
        await Check("press", new DivisorNotZeroRule(pendingOp, divisor)).ConfigureAwait(true);
        var next = State.ApplyKey(signal.Key, signal.By ?? Context.Caller?.ToString());
        await Show(next).ConfigureAwait(true);
    }

    [On]
    public Task OnClear(CalculatorClearSignal signal)
    {
        var next = State.ApplyKey("C", signal.By ?? Context.Caller?.ToString());
        return Show(next);
    }
}

public sealed class CalculatorSessionScenarioFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public CalculatorSessionScenarioFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task T4_CalculatorSession_InitialRead_RendersZeroAtGenerationOne()
    {
        var os = _fixture.OS;
        os.Register<InteractiveCalculatorScreen>();

        var brain = NewBrain("calc-init");
        var screenId = new NeuronId(brain, "app.interactive_calculator", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal("app.interactive_calculator", view.Alias);
        Assert.Equal(1L, view.Generation);

        var raw = view.View.GetRawText();
        Assert.Contains("Display: 0", raw);
        Assert.Contains("Accumulator: none", raw);
        Assert.Contains("PendingOp: none", raw);
    }

    [Fact]
    public async Task T4_CalculatorSession_MultiStepAddition_AdvancesGenerationMonotonically_AndProducesExactSum()
    {
        var os = _fixture.OS;
        os.Register<InteractiveCalculatorScreen>();

        var brain = NewBrain("calc-add");
        var screenId = new NeuronId(brain, "app.interactive_calculator", "default");
        var screen = os.Get<IScreen>(screenId);

        // Turn 1 -> 2: Press '4'
        await screen.Activate("app.interactive_calculator", 1L, "btn-4", by: "alice@intochat.io");
        await os.Idle();

        var view2 = await screen.Read();
        Assert.NotNull(view2);
        Assert.Equal(2L, view2.Generation);
        Assert.Contains("Display: 4", view2.View.GetRawText());
        Assert.Contains("LastCaller: alice@intochat.io", view2.View.GetRawText());

        // Turn 2 -> 3: Press '+'
        await screen.Activate("app.interactive_calculator", 2L, "btn-add", by: "alice@intochat.io");
        await os.Idle();

        var view3 = await screen.Read();
        Assert.NotNull(view3);
        Assert.Equal(3L, view3.Generation);
        Assert.Contains("Display: 4", view3.View.GetRawText());
        Assert.True(view3.View.GetRawText().Contains("PendingOp: +") || view3.View.GetRawText().Contains("PendingOp: \\u002B"));

        // Turn 3 -> 4: Press '5'
        await screen.Activate("app.interactive_calculator", 3L, "btn-5", by: "alice@intochat.io");
        await os.Idle();

        var view4 = await screen.Read();
        Assert.NotNull(view4);
        Assert.Equal(4L, view4.Generation);
        Assert.Contains("Display: 5", view4.View.GetRawText());

        // Turn 4 -> 5: Press '='
        await screen.Activate("app.interactive_calculator", 4L, "btn-equals", by: "alice@intochat.io");
        await os.Idle();

        var view5 = await screen.Read();
        Assert.NotNull(view5);
        Assert.Equal(5L, view5.Generation);
        Assert.Contains("Display: 9", view5.View.GetRawText());
    }

    [Fact]
    public async Task T4_CalculatorSession_ChainedMixedOperations_MultiplicationSubtraction_PreservesTurnHistory()
    {
        var os = _fixture.OS;
        os.Register<InteractiveCalculatorScreen>();

        var brain = NewBrain("calc-chain");
        var screenId = new NeuronId(brain, "app.interactive_calculator", "default");
        var screen = os.Get<IScreen>(screenId);

        // 12 * 3 - 6 = 30
        long gen = 1;
        // '1', '2' -> "12"
        await screen.Activate("app.interactive_calculator", gen++, "btn-1", by: "alice"); await os.Idle();
        await screen.Activate("app.interactive_calculator", gen++, "btn-2", by: "alice"); await os.Idle();

        // '*'
        await screen.Activate("app.interactive_calculator", gen++, "btn-mul", by: "alice"); await os.Idle();
        // '3'
        await screen.Activate("app.interactive_calculator", gen++, "btn-3", by: "alice"); await os.Idle();

        // '-' (evaluates 12 * 3 = 36 intermediate)
        await screen.Activate("app.interactive_calculator", gen++, "btn-sub", by: "alice"); await os.Idle();
        var interView = await screen.Read();
        Assert.NotNull(interView);
        Assert.Contains("Display: 36", interView.View.GetRawText());
        Assert.Contains("PendingOp: -", interView.View.GetRawText());

        // '6'
        await screen.Activate("app.interactive_calculator", gen++, "btn-6", by: "alice"); await os.Idle();
        // '='
        await screen.Activate("app.interactive_calculator", gen++, "btn-equals", by: "alice"); await os.Idle();

        var finalView = await screen.Read();
        Assert.NotNull(finalView);
        Assert.Equal(gen, finalView.Generation);
        Assert.Contains("Display: 30", finalView.View.GetRawText());
    }

    [Fact]
    public async Task T4_CalculatorSession_ClearButton_ResetsSessionStateToZero_IncrementingGeneration()
    {
        var os = _fixture.OS;
        os.Register<InteractiveCalculatorScreen>();

        var brain = NewBrain("calc-clear");
        var screenId = new NeuronId(brain, "app.interactive_calculator", "default");
        var screen = os.Get<IScreen>(screenId);

        await screen.Activate("app.interactive_calculator", 1L, "btn-7", by: "alice");
        await os.Idle();

        var view2 = await screen.Read();
        Assert.NotNull(view2);
        Assert.Contains("Display: 7", view2.View.GetRawText());

        // Press Clear
        await screen.Activate("app.interactive_calculator", 2L, "btn-clear", by: "alice");
        await os.Idle();

        var view3 = await screen.Read();
        Assert.NotNull(view3);
        Assert.Equal(3L, view3.Generation);
        Assert.Contains("Display: 0", view3.View.GetRawText());
    }

    [Fact]
    public async Task T4_CalculatorSession_Refusal_StaleGenerationActivation_ThrowsActivationRefusedExceptionStale()
    {
        var os = _fixture.OS;
        os.Register<InteractiveCalculatorScreen>();

        var brain = NewBrain("calc-stale");
        var screenId = new NeuronId(brain, "app.interactive_calculator", "default");
        var screen = os.Get<IScreen>(screenId);

        // Advance to generation 2
        await screen.Activate("app.interactive_calculator", 1L, "btn-9", by: "alice");
        await os.Idle();

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal(2L, view.Generation);

        // Attempt activation with stale generation 1L
        var ex = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Activate("app.interactive_calculator", 1L, "btn-5", by: "alice"));

        Assert.Equal(ActivationRefusedException.Stale, ex.Reason);
    }

    [Fact]
    public async Task T4_CalculatorSession_Refusal_DivisionByZero_ThrowsRuleViolationException_AndPermitsRecovery()
    {
        var os = _fixture.OS;
        os.Register<InteractiveCalculatorScreen>();

        var brain = NewBrain("calc-divzero");
        var screenId = new NeuronId(brain, "app.interactive_calculator", "default");
        var screen = os.Get<IScreen>(screenId);

        // Enter: 8 / 0
        await screen.Activate("app.interactive_calculator", 1L, "btn-8", by: "alice"); await os.Idle();
        await screen.Activate("app.interactive_calculator", 2L, "btn-div", by: "alice"); await os.Idle();
        await screen.Activate("app.interactive_calculator", 3L, "btn-0", by: "alice"); await os.Idle();

        // Equals evaluates 8 / 0 -> triggers DivisorNotZeroRule
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() =>
            screen.Activate("app.interactive_calculator", 4L, "btn-equals", by: "alice"));

        Assert.Contains("Cannot divide by zero.", ex.Message);

        // State remains at Generation 4 (turn transaction rolled back)
        var viewAfterRefusal = await screen.Read();
        Assert.NotNull(viewAfterRefusal);
        Assert.Equal(4L, viewAfterRefusal.Generation);

        // Recovery: user presses '4' instead, then '=' (8 / 4 = 2)
        await screen.Activate("app.interactive_calculator", 4L, "btn-4", by: "alice"); await os.Idle();
        await screen.Activate("app.interactive_calculator", 5L, "btn-equals", by: "alice"); await os.Idle();

        var viewRecovered = await screen.Read();
        Assert.NotNull(viewRecovered);
        Assert.Equal(6L, viewRecovered.Generation);
        Assert.Contains("Display: 2", viewRecovered.View.GetRawText());
    }

    [Fact]
    public async Task T4_CalculatorSession_NoOpInteraction_PreservesGeneration_WithoutViewChangedEmit()
    {
        var os = _fixture.OS;
        os.Register<InteractiveCalculatorScreen>();

        var brain = NewBrain("calc-noop");
        var screenId = new NeuronId(brain, "app.interactive_calculator", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Press '0' when display is already '0': state display remains "0"
        await screen.Activate("app.interactive_calculator", 1L, "btn-0", by: "alice");
        await os.Idle();

        var viewAfterNoop = await screen.Read();
        Assert.NotNull(viewAfterNoop);
        // Generation must NOT increment for identical render
        Assert.Equal(1L, viewAfterNoop.Generation);
    }
}
