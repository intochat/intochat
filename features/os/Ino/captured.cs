using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

// After a turn completes, Memory is asked to capture it as an episode ("memory.capture" with the conversation
// key and the run id). The signal carries only the status, so the run id is read from the chat on Completed.
// Nothing is asked when Memory is not installed. A run is asked once per session; Memory also skips a run
// whose episode exists, so a replayed signal or a restart never captures twice.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var chat = brain.Get<IChat>(Ino.DataKey(brain) + "/transcript");
var asked = new HashSet<string>();

await foreach (var change in brain.On<ChatRunChanged>(chat, brain.Stopping))
{
    if (change.Status != ChatRunStatus.Completed) { continue; }
    try
    {
        var state = await chat.Read();
        if (state.Run is not { Status: ChatRunStatus.Completed } run || !asked.Add(run.RunId)) { continue; }
        if (await Ino.MemoryKey(brain) is not { } memoryKey) { continue; }
        await brain.Get<IApp>(memoryKey).Request("capture",
            JsonSerializer.Serialize(new { agent = (await Ino.EnsureSelection(brain)).Agent, runId = run.RunId }));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Ino.Status(brain, "Could not ask Memory to capture the turn: " + Ino.Clip(error.Message)); }
}
