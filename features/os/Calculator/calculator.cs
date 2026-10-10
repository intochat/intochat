using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Text;

// The calculator keypad: each button click grows or evaluates the expression. State is the display
// neuron, so a restarted behavior resumes from what the person sees.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var display = brain.Get<IText>(appKey + "/display");
var keys = new[] { "7", "8", "9", "/", "4", "5", "6", "*", "1", "2", "3", "-", "C", "0", "=", "+" };
var buttons = Enumerable.Range(0, keys.Length).Select(index => (INeuron)brain.Get<IButton>(Name("key-" + index))).ToArray();

// Clicks are serialized by the loop itself: one reaction at a time, so no gate.
await foreach (var clicked in brain.On<ButtonClicked>(Sources.Of(buttons), brain.Stopping)) { await Press(clicked.Action); }

string Name(string part) => appKey + "/" + part;

async Task Press(string action)
{
    var expression = (await display.Read()).Markdown;
    await display.Set(action switch { "C" => "", "=" => Evaluate(expression), _ => expression + action });
}

static string Evaluate(string expression)
{
    if (expression.Length == 0) { return ""; }
    try { return Convert.ToString(new DataTable().Compute(expression, null)) ?? "Error"; }
    catch { return "Error"; }
}
