using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Button.Signals;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Flutter.Text;

// The two preference buttons post the request their action names; Save carries the draft and baseline.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
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
            Baseline((await brain.Get<IText>(appKey + "/preferences/baseline").Read()).Markdown)))
        : "";
    await app.Request(operation, input);
}

// A malformed baseline is sent as none, which Save rejects with a status, instead of crash-looping this synapse.
static PreferenceState? Baseline(string json)
{
    if (string.IsNullOrEmpty(json)) { return null; }
    try { return JsonSerializer.Deserialize<PreferenceState>(json); }
    catch (JsonException) { return null; }
}
