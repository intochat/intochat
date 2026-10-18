using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Select;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var key = Workspace.Key(brain);
await foreach (var selected in brain.On<SelectChanged>(Sources.Of(brain.Get<ISelect>(key + "/launcher"), brain.Get<ISelect>(key + "/more")), brain.Stopping))
{
    if (selected.Value.Length == 0) { continue; }
    var operation = "select:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(selected.Value)));
    await Workspace.Navigate(brain, selected.Value, operation);
}
