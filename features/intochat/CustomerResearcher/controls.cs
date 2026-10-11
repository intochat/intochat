using DigitalBrain.Apps;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

// Research and Stop post the same requests the assistant's tools do. The clicks neuron remembers the
// clicks already posted, so a click delivered again is not posted twice.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
var clicks = brain.Get<IText>(key + "/clicks");
var buttons = Sources.Of(brain.Get<IButton>(key + "/research"), brain.Get<IButton>(key + "/stop"));

await foreach (var clicked in brain.On<ButtonClicked>(buttons, brain.Stopping))
{
    var posted = (await clicks.Read()).Markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var id = clicked.RequestId.ToString("N");
    if (clicked.RequestId != Guid.Empty && posted.Contains(id)) { continue; }
    if (clicked.Action == "stop") { await app.Request("stop", ""); }
    else
    {
        var query = (await brain.Get<ITextField>(key + "/company").Read()).Value.Trim();
        if (query.Length is >= 1 and <= 500) { await app.Request("research", query); }
        else { await ResearchState.Status(brain, key).Set("Enter a company name (at most 500 characters)."); }
    }
    await clicks.Set(string.Join('\n', posted.TakeLast(31).Append(id)));
}
