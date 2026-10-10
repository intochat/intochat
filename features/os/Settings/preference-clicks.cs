using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.Text;

// The two preference buttons post the request their action names; Save carries the draft and baseline.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var buttons = Sources.Of(brain.Get<IButton>(appKey + "/save-preferences"), brain.Get<IButton>(appKey + "/reload-preferences"));
await foreach (var clicked in brain.On<ButtonClicked>(buttons, brain.Stopping))
{
    // Publisher.Name is the full grain id, so match by suffix.
    var operation = clicked.Publisher.Name.EndsWith("/" + appKey + "/save-preferences", StringComparison.Ordinal) ? "save-preferences" : "reload-preferences";
    var input = operation == "save-preferences"
        ? JsonSerializer.Serialize(new Submission((await brain.Get<ITextField>(appKey + "/preferences/display-name").Read()).Value,
            (await brain.Get<ITextField>(appKey + "/preferences/theme").Read()).Value,
            (await brain.Get<IText>(appKey + "/preferences/baseline").Read()).Markdown is { Length: > 0 } json ? JsonSerializer.Deserialize<PreferenceState>(json) : null))
        : "";
    await app.Request(operation, input);
}
