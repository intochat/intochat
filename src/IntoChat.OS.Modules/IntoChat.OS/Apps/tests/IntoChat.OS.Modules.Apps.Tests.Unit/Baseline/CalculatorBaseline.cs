/*
# Calculator
Role: Example
Shipped: false
Operations: open, clear, add

The folder script still draws the keypad. This class is the operation surface the generated baseline runs:
empty add shows Error on the display, and add of 2+3 shows 5.
*/
using System.Globalization;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Baseline;

[DigitalBrain.Signal("calculator.open")]
public sealed record CalculatorOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("calculator.clear")]
public sealed record CalculatorClear : DigitalBrain.Signal;

[DigitalBrain.Signal("calculator.add")]
public sealed record CalculatorAdd(string? Expression) : DigitalBrain.Signal;

[DigitalBrain.App("calculator")]
public sealed class CalculatorBaseline(IPresenter presenter) : DigitalBrain.App
{
    public string Display { get; private set; } = "";

    [DigitalBrain.On]
    public Task Open(CalculatorOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Show("");
        return Task.CompletedTask;
    }

    [DigitalBrain.On]
    public Task Clear(CalculatorClear request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Show("");
        return Task.CompletedTask;
    }

    [DigitalBrain.On]
    public Task Add(CalculatorAdd request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Show(Evaluate(request.Expression));
        return Task.CompletedTask;
    }

    private void Show(string value)
    {
        Display = value;
        Present(presenter.Present, static () => null, () => new View("Calculator", new Text(value.Length == 0 ? "0" : value)));
    }

    // The same sum the folder script's add.cs answers: two or more decimal terms, or the word Error.
    public static string Evaluate(string? input)
    {
        const NumberStyles number = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        var terms = (input ?? "").Split('+');
        var values = terms.Select(term => decimal.TryParse(term, number, CultureInfo.InvariantCulture, out var value) ? value : (decimal?)null).ToArray();
        if (values.Length < 2 || values.Any(value => value is null)) { return "Error"; }
        try { return values.Sum(value => value!.Value).ToString(CultureInfo.InvariantCulture); }
        catch (OverflowException) { return "Error"; }
    }
}
