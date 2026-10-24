using System.Globalization;
using IntoChat.OS;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Compute;
using IntoChat.OS.Flutter.Person;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

public sealed record TabModel(string Id, string App, string Title, bool Favourite);
public sealed record WindowModel(string Id, string Title, string ViewportKey, IReadOnlyList<TabModel> Tabs, string ActiveTabId, string ActiveApp);
public sealed record FavouriteModel(string Id, string Title, string Action, bool View);
public sealed record DockItem(string Key, string Title, string? Icon, bool Pinned, bool Open);
public sealed record DesktopModel(IReadOnlyList<WindowModel> Windows, string? FocusedId, IReadOnlyList<DockItem> Dock, bool Narrow, bool ShowGallery, string? Notice,
    string? Usage = null, string? Person = null, IReadOnlyList<GalleryEntry>? Entries = null, string? ConnectButtons = null, string? LayoutButton = null, string? SettingsButton = null,
    IReadOnlyList<FavouriteModel>? Favourites = null, string? Toast = null);

// Shared by the desktop's synapses: keys, state, and composing the model from the brain.
public static class Desktop
{
    // Background services and roots never appear in the dock.
    public static readonly HashSet<string> Hidden = [.. ShippedApps.Services];

    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string ViewKey(IntoChatOSConnection brain) => Key(brain) + "/view";
    public static string Scope(IntoChatOSConnection brain)
    {
        var at = Key(brain).IndexOf("/apps/", StringComparison.Ordinal);
        return at < 0 ? throw new InvalidOperationException("The desktop key is not an app key: " + Key(brain)) : Key(brain)[..at];
    }
    public static IWorkspace State(IntoChatOSConnection brain) => brain.Get<IWorkspace>(Scope(brain));
    // Whether the shell's window is narrow (< 900 logical px), as its `narrow` request last reported.
    public static IText Narrow(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/narrow");
    // Durable: set once Ino's greeting is seen, so later starts never greet again.
    // Durable: whether the apps gallery is showing, and the last Get's refusal shown in it.
    public static IText ShowGallery(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/gallery");
    public static IText Notice(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/notice");
    // The gallery's Connect buttons: one button neuron per connector under this prefix.
    public static string ConnectButtons(IntoChatOSConnection brain) => Key(brain) + "/connect";
    // The layout the next connect shows ("classic" or "desktop"); the kernel's root reads it.
    public static IText Layout(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/layout");
    // More's Classic layout: a button neuron whose activation asks the shell to switch and connect again.
    public static string LayoutButton(IntoChatOSConnection brain) => Key(brain) + "/classic";
    // More's Settings: a button neuron whose activation asks the shell to open the Settings app's window.
    public static string SettingsButton(IntoChatOSConnection brain) => Key(brain) + "/settings";
    public static string? ChosenLayout(string input) => input is "classic" or "desktop" ? input : null;
    public static string LayoutActivation(string brainId, string layout) => System.Text.Json.JsonSerializer.Serialize(
        new { kind = "reconnect", brainId, application = "desktop", operation = "layout", input = layout });
    public static IText Greeted(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/greeted");
    public static long Revision(IntoChatOSConnection brain) => long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture);

    // Durable: the last refusal (a cap reached), shown as a toast above the dock until a change succeeds.
    // It holds "<revision>\n<text>": shown only while the workspace is still at the revision it refused at,
    // so any later change (Classic, another app, a restart after one) hides it.
    public static IText Toast(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/toast");
    public static string ToastAt(long revision, string text) => revision.ToString(CultureInfo.InvariantCulture) + "\n" + text;
    public static string? ShownToast(string stored, long revision)
        => stored.IndexOf('\n', StringComparison.Ordinal) is var at and > 0 && long.TryParse(stored[..at], NumberStyles.None, CultureInfo.InvariantCulture, out var stamped)
            && stamped == revision && stored[(at + 1)..] is { Length: > 0 } text ? text : null;

    // The window holding the focused tab, even when that tab is hidden (the window then shows its last visible tab).
    public static string? FocusedWindow(string scope, WorkspaceState state)
        => Shown(scope, state).FirstOrDefault(shown => shown.Window.Tabs.Any(tab => tab.Id == state.FocusedViewId)).Window?.Id;

    // Hidden apps (the desktop itself, the workspace, services) never become windows: no recursion.
    public static WorkspaceViewRef[] Windowed(string scope, IEnumerable<WorkspaceViewRef> views)
        => [.. views.Where(Visible(scope))];

    // Whether the desktop shows a tab: its app is in this brain and not hidden. WindowOps counts caps by it.
    public static Func<WorkspaceViewRef, bool> Visible(string scope)
        => view => IApp.NameOf(scope, view.App) is { } name && !Hidden.Contains(name);

    // A window's visible tabs. Actions name a tab by its index here, so the layout and the scripts share it.
    public static WorkspaceViewRef[] Tabs(string scope, WorkspaceWindow window) => Windowed(scope, window.Tabs);

    // The visible tab at `index` of window `id`, or null if either is gone (a stale click is a no-op).
    public static string? TabAt(string scope, WorkspaceState state, string id, string index)
        => state.Windows.FirstOrDefault(window => window.Id == id) is { } window && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var i)
            && Tabs(scope, window) is var tabs && i < tabs.Length ? tabs[i].Id : null;

    // Splits `<windowId>/<index>`; window ids are w-<n>, so the last '/' separates them.
    public static (string Window, string Index) WindowAndIndex(string target)
        => target.LastIndexOf('/') is var at and >= 0 ? (target[..at], target[(at + 1)..]) : (target, "");

    // Windows with at least one visible tab, each with its active (visible) tab.
    private static (WorkspaceWindow Window, WorkspaceViewRef[] Tabs, WorkspaceViewRef Active)[] Shown(string scope, WorkspaceState state)
        => [.. state.Windows.Select(window => (window, tabs: Tabs(scope, window))).Where(shown => shown.tabs.Length > 0)
            .Select(shown => (shown.window, shown.tabs, WindowOps.ActiveTab(shown.window, Visible(scope))!))];

    // The app a new tab opens: the person's choice if it is a visible installed app, else Ino.
    public static string NewTabChoice(string? stored, IReadOnlySet<string> installed)
        => stored is { Length: > 0 } name && !Hidden.Contains(name) && installed.Contains(name) ? name : "ino";

    // The preference is a plain text field in this brain; reading it needs no semantic-type grant.
    public static async Task<string> NewTabApp(IntoChatOSConnection brain)
    {
        var stored = (await brain.Get<ITextField>(Scope(brain) + "/settings/workspace/new-tab-app").Read()).Value;
        var installed = new HashSet<string>(StringComparer.Ordinal);
        if (stored is { Length: > 0 } && await AppView(brain, stored) is not null) { installed.Add(stored); }
        return NewTabChoice(stored, installed);
    }

    // The full view of an installed, visible app (following a migration), or null.
    public static async Task<WorkspaceViewRef?> AppView(IntoChatOSConnection brain, string name)
    {
        var app = IApp.Key(Scope(brain), name);
        if (IApp.NameOf(Scope(brain), app) != name || Hidden.Contains(name)) { return null; }
        var installed = await brain.Get<IApp>(app).Read();
        return string.IsNullOrEmpty(installed.Manifest.Title) ? null : new(name, installed.MigrationTarget ?? app, "full", "");
    }

    public static async Task<DesktopModel> Model(IntoChatOSConnection brain, WorkspaceState state)
    {
        var installed = new Dictionary<string, AppSnapshot>(StringComparer.Ordinal);
        await foreach (var instance in brain.Kernel.Instances<IApp>(brain.Stopping))
        {
            if (IApp.NameOf(Scope(brain), instance.Key) is { } name && !Hidden.Contains(name))
            {
                var app = await brain.Get<IApp>(instance.Key).Read();
                if (!string.IsNullOrWhiteSpace(app.Manifest.Title) && app.MigrationTarget is null) { installed[name] = app; }
            }
        }
        string Name(WorkspaceViewRef view) => IApp.NameOf(Scope(brain), view.App) ?? view.Id;
        string Title(WorkspaceViewRef view) => (installed.GetValueOrDefault(Name(view))?.Manifest.Title ?? Name(view)) + (view.Variant is "full" or "" ? "" : " · " + view.Variant);
        var windows = Shown(Scope(brain), state).Select(shown => new WindowModel(shown.Window.Id, Title(shown.Active), Key(brain) + "/window/" + shown.Window.Id,
            [.. shown.Tabs.Select(tab => new TabModel(tab.Id, Name(tab), Title(tab), WindowOps.IsFavourite(state, tab)))], shown.Active.Id, Name(shown.Active))).ToArray();
        var pinned = state.Pins.Select(view => view.Id).ToHashSet(StringComparer.Ordinal);
        var open = windows.SelectMany(window => window.Tabs).Select(tab => tab.App).ToHashSet(StringComparer.Ordinal);
        var dock = DockOrder(installed.ToDictionary(pair => pair.Key, pair => pair.Value.Manifest.Title), pinned, open)
            .Select(name => new DockItem(name, installed[name].Manifest.Title, installed[name].Manifest.Icon,
                name == "ino" || pinned.Contains(name), open.Contains(name))).ToArray();
        // An app favourite opens (or focuses) its app; a view favourite opens its view as a new tab.
        var favourites = state.Favourites.Where(favourite => installed.ContainsKey(Name(favourite)))
            .Select(favourite => favourite.Variant == "" ? new FavouriteModel(favourite.Id, Title(favourite), "open:" + Name(favourite), false)
                : new FavouriteModel(favourite.Id, Title(favourite), "view:" + favourite.Id, true)).ToArray();
        var focused = FocusedWindow(Scope(brain), state);
        var toast = ShownToast((await Toast(brain).Read()).Markdown, state.Revision);
        var narrow = (await Narrow(brain).Read()).Markdown == "true";
        var gallery = (await ShowGallery(brain).Read()).Markdown == "true";
        var notice = (await Notice(brain).Read()).Markdown is { Length: > 0 } text ? text : null;
        // The gallery is read from the store only while it shows.
        var (entries, shown) = gallery ? await LoadGallery(() => brain.Get<IAppStore>(IAppStore.Key(Scope(brain))).List(), notice) : (null, notice);
        return new(windows, focused, dock, narrow, gallery, shown, await Usage(brain), await Person(brain), entries, ConnectButtons(brain), LayoutButton(brain), SettingsButton(brain),
            favourites, toast);
    }

    // Ino first, then pinned apps, then open apps; each group by title. Everything else is in the gallery.
    public static string[] DockOrder(IReadOnlyDictionary<string, string> titles, IReadOnlySet<string> pinned, IReadOnlySet<string> open)
        => [.. titles.Where(pair => pair.Key == "ino" || pinned.Contains(pair.Key) || open.Contains(pair.Key)).OrderBy(pair => pair.Key == "ino" ? 0 : pinned.Contains(pair.Key) ? 1 : 2)
            .ThenBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key)];

    // Usage and the person are optional: a brain without them still shows its dock.
    private static async Task<string?> Usage(IntoChatOSConnection brain)
    {
        try { return "$" + ((await brain.Get<ICompute>(Scope(brain) + "/compute").Read()).SpentMicros / 1_000_000m).ToString("0.00", CultureInfo.InvariantCulture); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private static async Task<string?> Person(IntoChatOSConnection brain)
    {
        try { return (await brain.Get<IPerson>(Scope(brain) + "/profile").Read()).DisplayName is { Length: > 0 } name ? name : null; }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    // A store that cannot answer still leaves the desktop (and an empty gallery) rendering.
    public static async Task<(IReadOnlyList<GalleryEntry> Entries, string? Notice)> LoadGallery(Func<Task<IReadOnlyList<GalleryEntry>>> list, string? notice)
    {
        try { return (await list(), notice); }
        catch (Exception error) when (error is not OperationCanceledException) { return ([], "The gallery could not be loaded."); }
    }

    // What the gallery says after Get: the refusal, or that the install worked but the window did not open.
    // A window cap's refusal already shows its toast, so the gallery adds nothing for it.
    public static string GetNotice(InstallOutcome outcome, bool opened, bool refused = false)
        => !outcome.Ok ? outcome.Message ?? "That app could not be installed."
            : opened || refused ? "" : "Installed " + outcome.InstalledName + ", but it could not be opened.";

    // What a Connect button hands the shell: open its connection window for this connector in this brain.
    public static string ConnectActivation(string brainId, string connectorId)
        => System.Text.Json.JsonSerializer.Serialize(new { kind = "connections", brainId, connector = connectorId });

    // What More's Settings hands the shell: open the Settings app's window in this brain, at a tab if given.
    public static string SettingsActivation(string brainId, string? tab = null) => tab is null
        ? System.Text.Json.JsonSerializer.Serialize(new { kind = "settings", brainId })
        : System.Text.Json.JsonSerializer.Serialize(new { kind = "settings", brainId, tab });

    // Arms More's Settings button with the settings activation (render and settings.cs both call it).
    public static async Task SetSettingsButton(IntoChatOSConnection brain)
    {
        var button = brain.Get<IButton>(SettingsButton(brain));
        var activation = SettingsActivation(Scope(brain));
        var current = await button.Read();
        if (current.Activation != activation || current.Label != "Settings") { await button.SetActivation("Settings", activation); }
    }

    // Each connectable connector's button carries its activation before the gallery references it.
    private static async Task SetConnectButtons(IntoChatOSConnection brain, IReadOnlyList<GalleryEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry is { Kind: "connection", Available: true, Installed: false, ConnectorId: not null }))
        {
            var button = brain.Get<IButton>(Gallery.ConnectButton(ConnectButtons(brain), entry.ConnectorId!));
            var activation = ConnectActivation(Scope(brain), entry.ConnectorId!);
            var current = await button.Read();
            if (current.Activation != activation || current.Label != "Connect") { await button.SetActivation("Connect", activation); }
        }
    }

    // Opens the installed app in a window beside the others (or focuses its window). Not opened if it
    // cannot; Refused when a cap stopped it (its toast then shows why).
    public static async Task<(bool Opened, bool Refused)> Open(IntoChatOSConnection brain, Guid request, string name)
    {
        var (opened, refused) = (false, false);
        var view = await AppView(brain, name);
        if (view is null) { return (false, false); }
        await Change(brain, request, state =>
        {
            var next = WindowOps.OpenApp(state, name, view, Visible(Scope(brain)));
            (opened, refused) = (next.Update is not null, next.Refusal is not null);
            return Task.FromResult(next);
        });
        return (opened, refused);
    }

    // Window verbs change the durable workspace; restore.cs re-renders on the resulting WorkspaceChanged.
    // A refusal (a cap reached) changes nothing and shows its toast; a stored change clears the toast.
    public static async Task Change(IntoChatOSConnection brain, Guid request, Func<WorkspaceState, Task<WindowChange>> change)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = await State(brain).Read();
            var next = await change(state);
            if (next.Refusal is { } refusal) { await Toast(brain).Set(ToastAt(state.Revision, refusal)); await Show(brain); return; }
            if (next.Update is not { } update) { return; }
            // Another change landed between read and apply: re-read once so the click is not dropped.
            try { await State(brain).Apply(update, "desktop:" + request, state.Revision); }
            catch (RevisionConflictException) when (attempt == 0) { continue; }
            // The new revision already hides it; clearing keeps the stored text from lingering.
            if ((await Toast(brain).Read()).Markdown is { Length: > 0 }) { await Toast(brain).Set(""); }
            return;
        }
    }

    // Each window is a viewport onto its app's view; the desktop tree only references it.
    public static async Task Show(IntoChatOSConnection brain)
    {
        // A concurrent publish or viewport change conflicts: retry once against fresh versions.
        try { await Render(brain); }
        catch (InvalidOperationException) { await Render(brain); }
    }

    private static async Task Render(IntoChatOSConnection brain)
    {
        // One read: the model and the viewports come from the same workspace state.
        var state = await State(brain).Read();
        var model = await Model(brain, state);
        // Each window's one viewport shows its active tab.
        foreach (var (window, _, view) in Shown(Scope(brain), state))
        {
            var viewport = brain.Get<IViewport>(Key(brain) + "/window/" + window.Id);
            var current = await viewport.Read();
            if (current.Target != view) { await viewport.Set(view, current.Revision); }
        }
        // Closed windows (and 0.1.3's per-view viewports) show nothing, even if an older render raced close.cs's own clear.
        var open = model.Windows.Select(window => window.ViewportKey).ToHashSet(StringComparer.Ordinal);
        await foreach (var instance in brain.Kernel.Instances<IViewport>(brain.Stopping))
        {
            if (!instance.Key.StartsWith(Key(brain) + "/window/", StringComparison.Ordinal) || open.Contains(instance.Key)) { continue; }
            var stale = brain.Get<IViewport>(instance.Key);
            var current = await stale.Read();
            if (current.Target is not null) { await stale.Set(null, current.Revision); }
        }
        if (model.Entries is { } shownEntries) { await SetConnectButtons(brain, shownEntries); }
        await SetSettingsButton(brain);
        var classic = brain.Get<IButton>(LayoutButton(brain));
        var switching = LayoutActivation(Scope(brain), "classic");
        if ((await classic.Read()).Activation != switching) { await classic.SetActivation("Classic layout", switching); }
        // A fresh publisher per render continues whichever version another synapse left.
        await new ViewPublisher(brain.Get<IView>(ViewKey(brain)), Revision(brain)).Publish(DesktopLayout.Render(model));
    }

    // Until Ino's greeting exists (Ino may reject it while answering), each start opens and focuses
    // Ino's window and asks it to greet; the greeting's fixed id keeps it to one.
    public static async Task FirstRun(IntoChatOSConnection brain)
    {
        if ((await Greeted(brain).Read()).Markdown == "true") { return; }
        var ino = IApp.Key(Scope(brain), "ino");
        if (string.IsNullOrEmpty((await brain.Get<IApp>(ino).Read()).Manifest.Title)) { return; }
        if ((await brain.Get<IChat>(ino + "/transcript").Read()).Selection is { } selection
            && (await brain.Get<IAgent>(selection.Agent).Read()).Runs.Any(run => run.Posted && run.RunId == "post/greeting"))
        { await Greeted(brain).Set("true"); return; }
        await Change(brain, Guid.NewGuid(), state => Task.FromResult(WindowOps.OpenApp(state, "ino", new WorkspaceViewRef("ino", ino, "full", ""), Visible(Scope(brain)))));
        await brain.Get<IApp>(ino).Request("open", "greet");
    }

    public static async Task Start(IntoChatOSConnection brain)
    {
        // Present even if the first render fails: restore.cs re-renders on the next change.
        try { await Show(brain); }
        finally
        {
            await brain.Get<IApp>(Key(brain)).Present(Revision(brain), new(new Sphere("icon.svg", []),
            Main: new(UIVocabulary.ViewType, ViewKey(brain)), Views: [new("full", new(UIVocabulary.ViewType, ViewKey(brain)))]));
        }
    }
}
