using DigitalBrain.Client;
using DigitalBrain.Flutter.Select;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var selected in brain.On<SelectChanged>(brain.Get<ISelect>(Workspace.Key(brain) + "/launcher"), brain.Stopping))
{
    if (selected.Value.Length == 0) { continue; }
    var operation = "select:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(selected.Value)));
    await Workspace.Navigate(brain, selected.Value, operation);
}
