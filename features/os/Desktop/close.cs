using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

// `close:<viewId>` closes that window; focus moves to the last remaining window.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("close:", StringComparison.Ordinal)) { continue; }
    var id = clicked.Action["close:".Length..];
    try
    {
        await Desktop.Change(brain, clicked.RequestId, state =>
        {
            if (!state.OpenViews.Any(view => view.Id == id)) { return Task.FromResult<WorkspaceUpdate?>(null); }
            WorkspaceViewRef[] views = [.. state.OpenViews.Where(view => view.Id != id)];
            var focus = state.FocusedViewId == id ? views.LastOrDefault()?.Id : state.FocusedViewId;
            return Task.FromResult<WorkspaceUpdate?>(new(views, focus, state.Pins));
        });
        var viewport = brain.Get<IViewport>(Desktop.Key(brain) + "/window/" + id);
        await viewport.Set(null, (await viewport.Read()).Revision);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not close " + id + ": " + error.Message); }
}
