using IntoChat.OS.Client;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;

// The desktop view's own actions. Each changes the durable workspace through Desktop.Change, and the workspace's change
// re-renders the desktop. A stale click (a tab, window or favourite already gone) does nothing; any other action is not ours.
public static class DesktopActions
{
    public static async Task Handle(IntoChatOSConnection brain, ViewAction clicked)
    {
        var action = clicked.Action;
        var request = clicked.RequestId;
        var scope = Desktop.Scope(brain);
        // `open:<app>` opens the app in a new window beside the others (or focuses the tab already showing it).
        if (Argument(action, "open:") is { } open) { await Desktop.Open(brain, request, open); }
        // `focus:<windowId>` focuses a window's active tab (on narrow screens the window fills the screen).
        else if (Argument(action, "focus:") is { } focus) { await Desktop.Change(brain, request, state => Task.FromResult(WindowOps.Focus(state, focus))); }
        // `close:<windowId>/<index>` closes that tab; closing a window's last tab closes the window and clears its viewport.
        else if (Argument(action, "close:") is { } close) { await Close(brain, request, scope, close); }
        // `tab:<windowId>/<index>` selects a tab.
        else if (Argument(action, "tab:") is { } tab)
        {
            var (id, index) = Desktop.WindowAndIndex(tab);
            await Desktop.Change(brain, request, state => Task.FromResult(Desktop.TabAt(scope, state, id, index) is { } selected
                ? WindowOps.SelectTab(state, id, selected) : new WindowChange(null, null)));
        }
        // `newtab` (the dock: the focused window) and `newtab:<windowId>` (a window's +) open the New tab app, Ino by default.
        else if (action == "newtab" || Argument(action, "newtab:") is not null)
        {
            var window = action == "newtab" ? null : action["newtab:".Length..];
            var name = await Desktop.NewTabApp(brain);
            if (await Desktop.AppView(brain, name) is not { } view) { return; }
            await Desktop.Change(brain, request, state => Task.FromResult(WindowOps.NewTab(state, window, name, view, Desktop.Visible(scope))));
        }
        // `favourite:<windowId>` stars or unstars the window's active tab.
        else if (Argument(action, "favourite:") is { } star)
        { await Desktop.Change(brain, request, state => Task.FromResult(WindowOps.ToggleFavourite(state, star, app => IApp.NameOf(scope, app) ?? "", Desktop.Visible(scope)))); }
        // `view:<favouriteId>` opens a view favourite as a new tab in the focused window (app favourites use `open:`).
        else if (Argument(action, "view:") is { } favourite)
        {
            await Desktop.Change(brain, request, state => Task.FromResult(
                state.Favourites.FirstOrDefault(item => item.Id == favourite) is { } view && IApp.NameOf(scope, view.App) is { } name
                    ? WindowOps.NewTab(state, null, name, view, Desktop.Visible(scope)) : new WindowChange(null, null)));
        }
        // `pin:<app>` / `unpin:<app>` keep an app in the dock across restarts (the workspace's Pins).
        else if (Argument(action, "pin:") is { } pin) { await Pin(brain, request, scope, pin, true); }
        else if (Argument(action, "unpin:") is { } unpin) { await Pin(brain, request, scope, unpin, false); }
        // A `settings` click from an older render re-arms More's Settings button, whose activation opens the Settings window.
        else if (action == "settings")
        {
            await Desktop.SetSettingsButton(brain);
            await Desktop.Show(brain);
        }
    }

    private static string? Argument(string action, string verb)
        => action.StartsWith(verb, StringComparison.Ordinal) ? action[verb.Length..] : null;

    private static async Task Close(IntoChatOSConnection brain, Guid request, string scope, string target)
    {
        var (id, index) = Desktop.WindowAndIndex(target);
        var gone = false;
        await Desktop.Change(brain, request, state =>
        {
            if (Desktop.TabAt(scope, state, id, index) is not { } tab) { return Task.FromResult(new WindowChange(null, null)); }
            var change = WindowOps.CloseTab(state, id, tab);
            gone = change.Update?.Windows?.All(window => window.Id != id) == true;
            return Task.FromResult(change);
        });
        if (!gone) { return; }
        var viewport = brain.Get<IViewport>(Desktop.Key(brain) + "/window/" + id);
        await viewport.Set(null, (await viewport.Read()).Revision);
    }

    // Ino is always first; hidden apps and unknown names are never pinned.
    private static Task Pin(IntoChatOSConnection brain, Guid request, string scope, string name, bool pin)
        => Desktop.Change(brain, request, async state =>
        {
            if (name == "ino" || Desktop.Hidden.Contains(name) || state.Pins.Any(view => view.Id == name) == pin) { return new WindowChange(null, null); }
            var app = IApp.Key(scope, name);
            if (IApp.NameOf(scope, app) != name) { return new WindowChange(null, null); }
            if (pin && string.IsNullOrEmpty((await brain.Get<IApp>(app).Read()).Manifest.Title)) { return new WindowChange(null, null); }
            WorkspaceViewRef[] pins = pin ? [.. state.Pins, new(name, app, "full", "")] : [.. state.Pins.Where(view => view.Id != name)];
            return new WindowChange(new WorkspaceUpdate(state.OpenViews, state.FocusedViewId, pins), null);
        });
}
