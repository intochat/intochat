using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;

// The answer to "result" is a neuron: {app}/result/{requestId} holds the stored row as JSON, or null.
// Reading it again for a redelivered request writes the same answer.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
var status = ResearchState.Status(brain, key);

await foreach (var request in brain.On(app, "result", brain.Stopping))
{
    var answer = brain.Get<IText>(key + "/result/" + request.RequestId.ToString("N"));
    var query = request.Input.Trim();
    if (query.Length == 0)
    {
        await answer.Set("null");
        await status.Set("Name the company whose result to read.");
        continue;
    }
    try { await answer.Set(await ResearchStore.Row(brain, key, query)); }
    catch (ScriptRefusalException error) { await answer.Set("null"); await status.Set(error.Message); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    {
        await answer.Set("null");
        await status.Set("Could not read stored research. Check the database connection and retry.");
    }
}
