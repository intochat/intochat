using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Button.Signals;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.TextField;

// The Send button posts the prompt as an "ask" request. Handled click ids are neuron state, so a click
// delivered twice asks once.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Jev.Key(brain));
var clicks = brain.Get<IText>(Jev.Name(brain, "clicks"));
await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(Jev.Name(brain, "send")), brain.Stopping))
{
    if (clicked.RequestId == Guid.Empty) { continue; }
    var seen = (await clicks.Read()).Markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var id = clicked.RequestId.ToString("N");
    if (seen.Contains(id)) { continue; }
    var text = (await brain.Get<ITextField>(Jev.Name(brain, "prompt")).Read()).Value.Trim();
    if (text.Length is >= 1 and <= 32000) { await app.Request("ask", text); }
    await clicks.Set(string.Join('\n', seen.TakeLast(31).Append(id)));
}
