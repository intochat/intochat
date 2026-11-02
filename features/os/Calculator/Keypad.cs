using System.Data;
using System.Globalization;
using DigitalBrain.UI;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Views;
using UI = DigitalBrain.UI.Ui;
using ViewUi = IntoChat.OS.Modules.Flutter.Views.Ui;

public sealed record CalculatorState(
    string Expression = "",
    string LastSum = "0",
    string[]? AppliedClicks = null)
{
    public CalculatorState() : this("", "0", [])
    {
    }
}

[DigitalBrain.App("calculator")]
public sealed class CalculatorScriptApp : DigitalBrain.App<CalculatorState>
{
    [DigitalBrain.On("open")]
    public Task Open()
    {
        Set(State);
        return Task.CompletedTask;
    }

    [DigitalBrain.On("clear")]
    public Task Clear()
    {
        Set(State with { Expression = "" });
        return Task.CompletedTask;
    }

    [DigitalBrain.On("add")]
    public Task Add(string? input)
    {
        var result = Keypad.Sum(input ?? "");
        Set(State with { Expression = result, LastSum = result });
        return Task.CompletedTask;
    }

    [DigitalBrain.On("press")]
    public Task Press(string? key)
    {
        var next = Keypad.Step(State.Expression, key ?? "");
        Set(State with { Expression = next });
        return Task.CompletedTask;
    }

    public View Render() => new(
        "Calculator",
        new Column(
        [
            new Surface(
                [
                    new Text(State.Expression.Length == 0 ? "0" : State.Expression),
                ],
                Kind: SurfaceKind.Elevated,
                Title: "Calculator",
                Subtitle: $"Last sum: {State.LastSum}"),
            new Grid(
                Columns: 4,
                Children:
                [
                    .. Keypad.Keys.Select((key, index) => (Node)new Button(
                        Keypad.Labels[index],
                        new ButtonAction("calculator", "press", [key]))),
                ],
                Gap: 8),
        ]));
}

// Shared by the calculator's synapses. The view neuron is the single declarative surface:
// creating the keypad does not create sixteen remote button grains.
public static class Keypad
{
    public static readonly string[] Keys = ["7", "8", "9", "/", "4", "5", "6", "*", "1", "2", "3", "-", "C", "0", "=", "+"];
    public static readonly string[] Labels = ["7", "8", "9", "÷", "4", "5", "6", "×", "1", "2", "3", "−", "C", "0", "=", "+"];

    public static UiNode Render(string expression) => ViewUi.Column("calculator",
        [ViewUi.Text("display", expression), .. Enumerable.Range(0, 4).Select(row =>
            ViewUi.Row("row-" + row, [.. Enumerable.Range(0, 4).Select(column =>
            {
                var index = row * 4 + column;
                return ViewUi.Button("key-" + index, Labels[index], Keys[index]);
            })]))], extents: [96, 80, 80, 80, 80]);

    public static async Task Press(IntoChatOSConnection brain, string action, Guid click)
    {
        _ = click;
        var appKey = brain.Setting("App")!;
        var view = brain.Get<IView>(appKey + "/view");
        var current = await view.Read();
        var currentExpr = current?.Root.Children?.FirstOrDefault()?.Text ?? "";
        var expression = Step(currentExpr, action);
        await Publish(brain, expression);
    }

    public static async Task Publish(IntoChatOSConnection brain, string expression)
    {
        var appKey = brain.Setting("App")!;
        var publisher = new ViewPublisher(
            brain.Get<IView>(appKey + "/view"),
            long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture));
        await publisher.Publish(Render(expression));
    }

    public static string Step(string expression, string action) => action switch
    {
        "C" => "",
        "=" => Evaluate(expression),
        _ => (expression == "Error" ? "" : expression) + action,
    };

    public static string Sum(string input)
    {
        const NumberStyles number = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        var terms = input.Split('+');
        var values = terms.Select(term => decimal.TryParse(term, number, CultureInfo.InvariantCulture, out var value) ? value : (decimal?)null).ToArray();
        if (values.Length < 2 || values.Any(value => value is null)) { return "Error"; }
        try { return values.Sum(value => value!.Value).ToString(CultureInfo.InvariantCulture); }
        catch (OverflowException) { return "Error"; }
    }

    private static string Evaluate(string expression)
    {
        if (expression.Length == 0) { return ""; }
        try
        {
            var value = new System.Data.DataTable().Compute(expression, null);
            return value is double number && !double.IsFinite(number) ? "Error" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Error";
        }
        catch (Exception) { return "Error"; }
    }
}
