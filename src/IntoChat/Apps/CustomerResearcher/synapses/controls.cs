using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

// The UI controls publish the same requests the assistant's tools do.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);

await Task.WhenAll(ResearchClicks(), StopClicks());

async Task ResearchClicks()
{
    await foreach (var _ in brain.On<ButtonClicked>(brain.Get<IButton>(key + "/research"), brain.Stopping))
    {
        var query = (await brain.Get<ITextField>(key + "/company").Read()).Value.Trim();
        if (query.Length is >= 1 and <= 500) { await app.Request("research", query); }
        else { await brain.Get<IText>(key + "/status").Set("Enter a company name (at most 500 characters)."); }
    }
}

async Task StopClicks()
{
    await foreach (var _ in brain.On<ButtonClicked>(brain.Get<IButton>(key + "/stop"), brain.Stopping))
    { await app.Request("stop", ""); }
}
