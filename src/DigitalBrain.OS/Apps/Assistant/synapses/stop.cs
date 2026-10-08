using System.Text.Json;
using DigitalBrain.AI.Agents;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Text;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(key + "/stop"), brain.Stopping))
{
    if (string.IsNullOrEmpty(clicked.Action)) { continue; }
    try
    {
        var command = JsonSerializer.Deserialize<JsonElement>(clicked.Action);
        var source = command.GetProperty("source").GetString() ?? "";
        var runId = command.GetProperty("runId").GetString() ?? "";
        if (!source.StartsWith(key + "/conversations/", StringComparison.Ordinal) || runId.Length == 0) { continue; }
        await brain.Get<IAgent>(source).CancelRun(runId);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await brain.Get<IText>(key + "/status").Set("Could not stop the response. Try Stop again. " + error.Message);
    }
}
