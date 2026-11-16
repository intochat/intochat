/*
# Calculator
Role: Default
Shipped: true
Operations: open, clear, add, press

A keypad that evaluates arithmetic expressions, sums, and inline button presses in a single declarative View.
*/
using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Calculator;

[Signal("calculator.open")]
public sealed record CalculatorOpen : Signal;

[Signal("calculator.clear")]
public sealed record CalculatorClear : Signal;

[Signal("calculator.add")]
public sealed record CalculatorAdd(string? Expression = "2+3") : Signal;

[Signal("calculator.press")]
public sealed record CalculatorPress(string? Key = "7") : Signal;

public sealed record CalculatorAppState(string Expression = "", string LastSum = "0", string[]? History = null)
{
    public CalculatorAppState() : this("", "0", []) { }

    [Invariant("Calculator history is capped at 8 entries."), JsonIgnore]
    public bool HistoryBounded => (History?.Length ?? 0) <= 8;

    [Invariant("LastSum is never empty."), JsonIgnore]
    public bool LastSumNonEmpty => !string.IsNullOrWhiteSpace(LastSum);

    public Rule HasClearableDisplay() => Decision.Rule("display_already_empty", Expression.Length == 0, "Display is already clear.");
    public Rule NonEmptyKey(string? key) => Decision.Rule("empty_key", string.IsNullOrWhiteSpace(key), "Keypad key is required.");

    public Decision<CalculatorAppState> PressKey([Sample("7", "+", "3", "=", "C", "0")] string key)
    {
        if (Decision.Check("press-key", NonEmptyKey(key)) is { } refusal) { return refusal; }
        var trimmed = key.Trim();
        var next = CalculatorApp.Step(Expression, trimmed);
        var history = trimmed == "=" && Expression.Length > 0
            ? [.. (History ?? []).Append($"{Expression} = {next}").TakeLast(8)]
            : History ?? [];
        return Decision.Accept(this with { Expression = next, History = history });
    }

    public Decision<CalculatorAppState> ClearDisplay() =>
        Decision.Check("clear-display", HasClearableDisplay()) ?? Decision.Accept(this with { Expression = "" });

    public Decision<CalculatorAppState> AddSum([Sample("2+3", "42")] string expression)
    {
        if (Decision.Check("add-sum", Decision.Rule("empty_sum", string.IsNullOrWhiteSpace(expression), "Sum expression is required.")) is { } refusal)
        {
            return refusal;
        }

        var trimmed = expression.Trim();
        var result = CalculatorApp.EvaluateSum(trimmed);
        return Decision.Accept(this with
        {
            Expression = result,
            LastSum = result,
            History = [.. (History ?? []).Append($"{trimmed} = {result}").TakeLast(8)],
        });
    }
}

[Neuron("app.calculator"), Screen("app.calculator", Title = "Calculator")]
public sealed class CalculatorApp : AggregateScreen<CalculatorAppState>
{
    public static readonly string[] Keys = ["7", "8", "9", "/", "4", "5", "6", "*", "1", "2", "3", "-", "C", "0", "=", "+"];
    public static readonly string[] Labels = ["7", "8", "9", "÷", "4", "5", "6", "×", "1", "2", "3", "−", "C", "0", "=", "+"];

    public static AppBundle Bundle { get; } = new(
        "calculator",
        "2.0.0",
        new AppManifest(
            Title: "Calculator",
            Description: "A keypad that evaluates arithmetic expressions.",
            Operations:
            [
                new("open", "Open the calculator keypad."),
                new("clear", "Empty the display."),
                new("add", "Add numbers written as \"a+b\"; the sum is stored in LastSum, or \"Error\" when the input is not a sum."),
                new("press", "Press a keypad button."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Toolbar: [new("clear", Icon: "backspace", Label: "Clear")]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(CalculatorApp).AssemblyQualifiedName,
    };

    public new CalculatorAppState State => base.State;
    public View Render() => PreviewView(State);

    [On]
    public Task Open(CalculatorOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Show(State);
    }

    [On]
    public Task Clear(CalculatorClear request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return State.Expression.Length == 0 ? Show(State) : ApplyDecisionAsync(State.ClearDisplay());
    }

    [On]
    public Task Add(CalculatorAdd request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDecisionAsync(State.AddSum(string.IsNullOrWhiteSpace(request.Expression) ? "2+3" : request.Expression.Trim()));
    }

    [On]
    public Task Press(CalculatorPress request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDecisionAsync(State.PressKey(string.IsNullOrWhiteSpace(request.Key) ? "7" : request.Key.Trim()));
    }

    public static string Step(string expression, string action) => action switch
    {
        "C" => "",
        "=" => Evaluate(expression),
        _ => (expression == "Error" ? "" : expression) + action,
    };

    public static string EvaluateSum(string input)
    {
        const NumberStyles number = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        var values = input.Split('+').Select(t => decimal.TryParse(t, number, CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null).ToArray();
        if (values.Length < 2 || values.Any(v => v is null)) { return "Error"; }

        try { return values.Sum(v => v!.Value).ToString(CultureInfo.InvariantCulture); }
        catch (OverflowException error)
        {
            OsConsoleLog.For<CalculatorApp>().LogSwallowed(error, "Overflow in EvaluateSum returns Error.");
            return "Error";
        }
    }

    private static string Evaluate(string expression)
    {
        if (expression.Length == 0) { return ""; }
        try
        {
            using var table = new System.Data.DataTable();
            var value = table.Compute(expression, null);
            return value is double n && !double.IsFinite(n) ? "Error" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Error";
        }
        catch (Exception error)
        {
            OsConsoleLog.For<CalculatorApp>().LogSwallowed(error, "Malformed arithmetic expression evaluates to Error.");
            return "Error";
        }
    }

    protected override Node Render(CalculatorAppState state)
    {
        var display = state.Expression.Length == 0 ? "0" : state.Expression;
        var isError = display == "Error";
        var history = state.History ?? [];

        return VStack(Space.Md)[
            HeaderBar(
                "Calculator",
                "Arithmetic & sum evaluator",
                StatusPill(isError ? StatusKind.Error : StatusKind.Done, isError ? "Error" : "Ready"),
                GhostButton("Clear", Icons.Trash2).Key("clear-display").Small().OnClick<CalculatorAppState>(x => x.ClearDisplay()).Guard(state.HasClearableDisplay())),
            Grid(2, Space.Sm)[
                StatCard("kpi-calc-display", "Display", display, state.Expression.Length == 0 ? "Idle" : "Active expression", isError ? BadgeTone.Error : BadgeTone.Accent, "calculator"),
                StatCard("kpi-calc-sum", "Last Sum", state.LastSum, $"{history.Length}/8 history", BadgeTone.Done, Icons.Plus)
            ],
            Grid(4, Space.Sm, [.. Keys.Select((key, i) => (key == "=" ? PrimaryButton(Labels[i]) : Button(Labels[i])).Key($"key-{i}").OnClick<CalculatorAppState>(x => x.PressKey(key)).ToNode())]),
            history.Length > 0
                ? Table("Recent Calculations", ["#", "Expression"], history.Select((entry, i) => (IReadOnlyList<string>)[$"{i + 1}", entry]).ToArray())
                : null
        ];
    }
}
