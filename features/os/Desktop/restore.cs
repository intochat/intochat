using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Workspace.Signals;

// Re-renders the desktop whenever the durable workspace changes: window verbs, shell
// activation and deep links all land here. On restart the windows come back from that state.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var changed in brain.On<WorkspaceChanged>(Desktop.State(brain), brain.Stopping))
{
    try { await Desktop.Show(brain); }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not re-render: " + error.Message); }
}
