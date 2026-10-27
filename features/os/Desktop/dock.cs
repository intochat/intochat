using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;

// `pin:<app>` / `unpin:<app>` keep an app in the dock across restarts (the workspace's Pins).
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    var pin = clicked.Action.StartsWith("pin:", StringComparison.Ordinal);
    if (!pin && !clicked.Action.StartsWith("unpin:", StringComparison.Ordinal)) { continue; }
    var name = clicked.Action[(pin ? "pin:" : "unpin:").Length..];
    try
    {
        await Desktop.Change(brain, clicked.RequestId, async state =>
        {
            // Ino is always first; hidden apps and unknown names are never pinned.
            if (name == "ino" || Desktop.Hidden.Contains(name) || state.Pins.Any(view => view.Id == name) == pin) { return new WindowChange(null, null); }
            var app = IApp.Key(Desktop.Scope(brain), name);
            if (IApp.NameOf(Desktop.Scope(brain), app) != name) { return new WindowChange(null, null); }
            if (pin && string.IsNullOrEmpty((await brain.Get<IApp>(app).Read()).Manifest.Title)) { return new WindowChange(null, null); }
            WorkspaceViewRef[] pins = pin ? [.. state.Pins, new(name, app, "full", "")] : [.. state.Pins.Where(view => view.Id != name)];
            return new WindowChange(new WorkspaceUpdate(state.OpenViews, state.FocusedViewId, pins), null);
        });
    }
    // One bad click must not end the subscription.
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not " + (pin ? "pin " : "unpin ") + name + ": " + error.Message); }
}
