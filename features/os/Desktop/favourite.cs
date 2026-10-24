using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `favourite:<windowId>` stars or unstars the window's active tab; `view:<favouriteId>` opens a view
// favourite as a new tab in the focused window (app favourites use `open:`).
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    var star = clicked.Action.StartsWith("favourite:", StringComparison.Ordinal);
    if (!star && !clicked.Action.StartsWith("view:", StringComparison.Ordinal)) { continue; }
    var id = clicked.Action[(star ? "favourite:" : "view:").Length..];
    var scope = Desktop.Scope(brain);
    try
    {
        await Desktop.Change(brain, clicked.RequestId, state => Task.FromResult(star
            ? WindowOps.ToggleFavourite(state, id, app => IApp.NameOf(scope, app) ?? "")
            // A favourite removed meanwhile is a stale click: nothing to open.
            : state.Favourites.FirstOrDefault(favourite => favourite.Id == id) is { } view && IApp.NameOf(scope, view.App) is { } name
                ? WindowOps.NewTab(state, null, name, view) : new WindowChange(null, null)));
    }
    // One bad click must not end the subscription.
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not " + clicked.Action + ": " + error.Message); }
}
