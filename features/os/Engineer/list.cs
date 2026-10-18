using IntoChat.OS.Flutter.Text.Signals;

// The list follows every request: each change to a request is followed by a change of {app}/changes, and
// the list and the status line are rebuilt from the requests, once when this starts and after each burst.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await brain.React<TextChanged>(Engineer.Changes(brain), _ => Engineer.RenderAsync(brain), brain.Stopping);
