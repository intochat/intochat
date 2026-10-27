using System.Data;
using System.Globalization;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Views;

// Shared by the calculator's synapses. The keypad neuron is the truth: its first line is the
// expression, the other lines are the clicks already applied, so a click delivered again is not
// pressed twice. The display only mirrors the expression and is rewritten from it on every press.
public static class Keypad
{
    public static readonly string[] Keys = ["7", "8", "9", "/", "4", "5", "6", "*", "1", "2", "3", "-", "C", "0", "=", "+"];
    public static readonly string[] Labels = ["7", "8", "9", "÷", "4", "5", "6", "×", "1", "2", "3", "−", "C", "0", "=", "+"];

    private const int RememberedClicks = 32;

    // A view is a value. Creating the keypad does not create sixteen remote buttons.
    public static UiNode Render(string expression) => Ui.Column("calculator",
        [Ui.Text("display", expression), .. Enumerable.Range(0, 4).Select(row =>
            Ui.Row("row-" + row, [.. Enumerable.Range(0, 4).Select(column =>
            {
                var index = row * 4 + column;
                return Ui.Button("key-" + index, Labels[index], Keys[index]);
            })]))], extents: [96, 80, 80, 80, 80]);

    public static async Task Press(IntoChatOSConnection brain, string action, Guid click)
    {
        var appKey = brain.Setting("App")!;
        var keypad = brain.Get<IText>(appKey + "/keypad");
        var lines = (await keypad.Read()).Markdown.Split('\n');
        var expression = lines[0];
        var applied = lines.Skip(1).ToArray();
        var id = click.ToString();
        if (click == Guid.Empty || !applied.Contains(id))
        {
            expression = Next(expression, action);
            string[] next = [expression, .. applied.TakeLast(RememberedClicks - 1), id];
            await keypad.Set(string.Join('\n', next));
        }
        await brain.Get<IText>(appKey + "/display").Set(expression);
    }

    private static string Next(string expression, string action) => action switch
    {
        "C" => "",
        "=" => Evaluate(expression),
        _ => (expression == "Error" ? "" : expression) + action,
    };

    private static string Evaluate(string expression)
    {
        if (expression.Length == 0) { return ""; }
        try
        {
            var value = new DataTable().Compute(expression, null);
            return value is double number && !double.IsFinite(number) ? "Error" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Error";
        }
        catch (Exception) { return "Error"; }
    }
}
