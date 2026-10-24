using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var key = Ino.DataKey(brain);
await foreach (var clicked in brain.On<ChatActionActivated>(brain.Get<IChat>(key + "/transcript"), brain.Stopping))
{
    if (!clicked.ActionId.StartsWith("stop/", StringComparison.Ordinal)) { continue; }
    try
    {
        var command = JsonSerializer.Deserialize<JsonElement>(clicked.Payload);
        var source = clicked.Source;
        var runId = command.GetProperty("runId").GetString() ?? "";
        if (!source.StartsWith(Ino.Scope(brain) + "/", StringComparison.Ordinal) || runId.Length == 0
            || clicked.ActionId != "stop/" + runId) { continue; }
        await brain.Get<IAgent>(source).CancelRun(runId);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await Ino.Status(brain, Ino.Failure(InoCopy.StopFailed, error));
    }
}
