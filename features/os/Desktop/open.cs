using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

// `open:<app>` opens the app in a window beside the others (or focuses its window).
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("open:", StringComparison.Ordinal)) { continue; }
    var name = clicked.Action["open:".Length..];
    try
    {
        await Desktop.Change(brain, clicked.RequestId, async state =>
        {
            var app = IApp.Key(Desktop.Scope(brain), name);
            if (IApp.NameOf(Desktop.Scope(brain), app) != name || Desktop.Hidden.Contains(name)) { return null; }
            var installed = await brain.Get<IApp>(app).Read();
            if (string.IsNullOrEmpty(installed.Manifest.Title)) { return null; }
            app = installed.MigrationTarget ?? app;
            var existing = state.OpenViews.FirstOrDefault(view => view.App == app);
            var views = existing is null ? [.. state.OpenViews, new WorkspaceViewRef(name, app, "full", "")] : state.OpenViews;
            return new WorkspaceUpdate(views, existing?.Id ?? name, state.Pins);
        });
    }
    // One bad click must not end the subscription.
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not open " + name + ": " + error.Message); }
}
