using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

// `focus:<viewId>` focuses an open window (on narrow screens it fills the screen).
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("focus:", StringComparison.Ordinal)) { continue; }
    var id = clicked.Action["focus:".Length..];
    try
    {
        await Desktop.Change(brain, clicked.RequestId, state => Task.FromResult<WorkspaceUpdate?>(
            state.FocusedViewId == id || !state.OpenViews.Any(view => view.Id == id) ? null : new(state.OpenViews, id, state.Pins)));
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not focus " + id + ": " + error.Message); }
}
