/*
# Calculator
Role: Default
Shipped: true
Operations: open, clear, add, press

A keypad that evaluates arithmetic expressions, sums, and inline button presses in a single declarative View.
*/
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Apps.Calculator;

[Signal("calculator.open")]
public sealed record CalculatorOpen : Signal;

[Signal("calculator.clear")]
public sealed record CalculatorClear : Signal;

[Signal("calculator.add")]
public sealed record CalculatorAdd(string? Expression = "2+3") : Signal;

[Signal("calculator.press")]
public sealed record CalculatorPress(string? Key = "7") : Signal;

public sealed record CalculatorAppState(
    string Expression = "",
    string LastSum = "0",
    string[]? History = null)
{
    public CalculatorAppState() : this("", "0", [])
    {
    }
}

[Neuron("app.calculator")]
public sealed class CalculatorApp : Screen<CalculatorAppState>
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
            Toolbar:
            [
                new("clear", Icon: "backspace", Label: "Clear"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(CalculatorApp).AssemblyQualifiedName,
    };

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
        return Show(State with { Expression = "" });
    }

    [On]
    public Task Add(CalculatorAdd request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = string.IsNullOrWhiteSpace(request.Expression) ? "2+3" : request.Expression.Trim();
        var result = EvaluateSum(input);
        var history = (State.History ?? []).Append($"{input} = {result}").TakeLast(8).ToArray();
        return Show(State with { Expression = result, LastSum = result, History = history });
    }

    [On]
    public Task Press(CalculatorPress request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = string.IsNullOrWhiteSpace(request.Key) ? "7" : request.Key.Trim();
        var next = Step(State.Expression, key);
        var history = key == "=" && State.Expression.Length > 0
            ? (State.History ?? []).Append($"{State.Expression} = {next}").TakeLast(8).ToArray()
            : State.History ?? [];
        return Show(State with { Expression = next, History = history });
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
        var terms = input.Split('+');
        var values = terms.Select(term => decimal.TryParse(term, number, CultureInfo.InvariantCulture, out var value) ? value : (decimal?)null).ToArray();
        if (values.Length < 2 || values.Any(value => value is null))
        {
            return "Error";
        }

        try
        {
            return values.Sum(value => value!.Value).ToString(CultureInfo.InvariantCulture);
        }
        catch (OverflowException error)
        {
            NullLogger.Instance.LogSwallowed(error, "Overflow in EvaluateSum returns Error.");
            return "Error";
        }
    }

    private static string Evaluate(string expression)
    {
        if (expression.Length == 0)
        {
            return "";
        }

        try
        {
            using var table = new System.Data.DataTable();
            var value = table.Compute(expression, null);
            return value is double number && !double.IsFinite(number)
                ? "Error"
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Error";
        }
        catch (Exception error)
        {
            NullLogger.Instance.LogSwallowed(error, "Malformed arithmetic expression evaluates to Error.");
            return "Error";
        }
    }

    public new CalculatorAppState State => base.State;
    public View Render() => Render(State);

    private static new Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
        return new Emit("app.calculator", alias, JsonSerializer.SerializeToElement(signal));
    }
    protected override View Render(CalculatorAppState state)
    {
        var display = state.Expression.Length == 0 ? "0" : state.Expression;
        var history = state.History ?? [];

        return new(
            "Calculator",
            new Column(
            [
                new Surface(
                    [
                        new Heading(display, Level: 1, Subtitle: $"Last sum: {state.LastSum}"),
                        new Row(
                        [
                            new TagPill($"Display: {display}", "accent", "calculator"),
                            new TagPill($"Sum: {state.LastSum}", "ok", "plus"),
                        ]) { Gap = 6 },
                    ],
                    Kind: SurfaceKind.Elevated,
                    Title: "Calculator",
                    Subtitle: "A keypad that evaluates arithmetic expressions."),
                new Grid(
                    Columns: 4,
                    Children:
                    [
                        .. Keys.Select((key, index) => (Node)new Button(
                            $"key-{index}",
                            Labels[index],
                            Emit: Emits(new CalculatorPress(key)),
                            Tone: key is "=" or "+" or "-" or "*" or "/" ? ButtonTone.Accent : ButtonTone.Neutral)),
                    ],
                    Gap: 8),
                .. history.Length > 0
                    ? new Node[]
                    {
                        new Surface(
                            history.Select(entry => (Node)new Text(entry)).ToArray(),
                            Kind: SurfaceKind.Acrylic,
                            Title: "History"),
                    }
                    : [],
            ])
            { Gap = 12 });
    }
}
