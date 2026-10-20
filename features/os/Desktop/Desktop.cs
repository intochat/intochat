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
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

public sealed record WindowModel(string Id, string App, string Title, string ViewportKey);
public sealed record DockItem(string Key, string Title, string? Icon, bool Pinned, bool Open);
public sealed record DesktopModel(IReadOnlyList<WindowModel> Windows, string? FocusedId, IReadOnlyList<DockItem> Dock, bool Narrow, bool ShowGallery, string? Notice,
    string? Usage = null, string? Person = null, IReadOnlyList<GalleryEntry>? Entries = null, string? ConnectButtons = null, string? LayoutButton = null);

// Shared by the desktop's synapses: keys, state, and composing the model from the brain.
public static class Desktop
{
    // Background services and roots never appear in the dock.
    public static readonly HashSet<string> Hidden = ["desktop", "workspace", "router", "selfimprove", "circleroom", "usage"];

    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string ViewKey(IntoChatOSConnection brain) => Key(brain) + "/view";
    public static string Scope(IntoChatOSConnection brain)
    {
        var at = Key(brain).IndexOf("/apps/", StringComparison.Ordinal);
        return at < 0 ? throw new InvalidOperationException("The desktop key is not an app key: " + Key(brain)) : Key(brain)[..at];
    }
    public static IWorkspace State(IntoChatOSConnection brain) => brain.Get<IWorkspace>(Scope(brain));
    // Until the shell reports its width, the narrow flag is the desktop's own state (set by `narrow:<bool>`).
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
    public static string? ChosenLayout(string input) => input is "classic" or "desktop" ? input : null;
    public static string LayoutActivation(string brainId, string layout) => System.Text.Json.JsonSerializer.Serialize(
        new { kind = "reconnect", brainId, application = "desktop", operation = "layout", input = layout });
    public static IText Greeted(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/greeted");
    public static long Revision(IntoChatOSConnection brain) => long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture);

    // Hidden apps (the desktop itself, the workspace, services) never become windows: no recursion.
    public static WorkspaceViewRef[] Windowed(string scope, IEnumerable<WorkspaceViewRef> views)
        => [.. views.Where(view => IApp.NameOf(scope, view.App) is { } name && !Hidden.Contains(name))];

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
        string Title(string name) => installed.GetValueOrDefault(name)?.Manifest.Title ?? name;
        var windows = Windowed(Scope(brain), state.OpenViews).Select(view => new WindowModel(view.Id, view.App,
            Title(IApp.NameOf(Scope(brain), view.App) ?? view.Id), Key(brain) + "/window/" + view.Id)).ToArray();
        var pinned = state.Pins.Select(view => view.Id).ToHashSet(StringComparer.Ordinal);
        var open = windows.Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
        var dock = DockOrder(installed.ToDictionary(pair => pair.Key, pair => pair.Value.Manifest.Title), pinned, open)
            .Select(name => new DockItem(name, installed[name].Manifest.Title, installed[name].Manifest.Icon,
                name == "ino" || pinned.Contains(name), windows.Any(window => window.Id == name))).ToArray();
        var narrow = (await Narrow(brain).Read()).Markdown == "true";
        var gallery = (await ShowGallery(brain).Read()).Markdown == "true";
        var notice = (await Notice(brain).Read()).Markdown is { Length: > 0 } text ? text : null;
        // The gallery is read from the store only while it shows.
        var (entries, shown) = gallery ? await LoadGallery(() => brain.Get<IAppStore>(IAppStore.Key(Scope(brain))).List(), notice) : (null, notice);
        return new(windows, state.FocusedViewId, dock, narrow, gallery, shown, await Usage(brain), await Person(brain), entries, ConnectButtons(brain), LayoutButton(brain));
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
    public static string GetNotice(InstallOutcome outcome, bool opened)
        => !outcome.Ok ? outcome.Message ?? "That app could not be installed."
            : opened ? "" : "Installed " + outcome.InstalledName + ", but it could not be opened.";

    // What a Connect button hands the shell: open its connection window for this connector in this brain.
    public static string ConnectActivation(string brainId, string connectorId)
        => System.Text.Json.JsonSerializer.Serialize(new { kind = "connections", brainId, connector = connectorId });

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

    // Opens the installed app in a window beside the others (or focuses its window); false if it cannot.
    public static async Task<bool> Open(IntoChatOSConnection brain, Guid request, string name)
    {
        var opened = false;
        await Change(brain, request, async state =>
        {
            var next = await Opening(brain, state, name);
            opened = next is not null;
            return next;
        });
        return opened;
    }

    private static async Task<WorkspaceUpdate?> Opening(IntoChatOSConnection brain, WorkspaceState state, string name)
    {
        var app = IApp.Key(Scope(brain), name);
        if (IApp.NameOf(Scope(brain), app) != name || Hidden.Contains(name)) { return null; }
        var installed = await brain.Get<IApp>(app).Read();
        if (string.IsNullOrEmpty(installed.Manifest.Title)) { return null; }
        app = installed.MigrationTarget ?? app;
        var existing = state.OpenViews.FirstOrDefault(view => view.App == app);
        var views = existing is null ? [.. state.OpenViews, new WorkspaceViewRef(name, app, "full", "")] : state.OpenViews;
        return new WorkspaceUpdate(views, existing?.Id ?? name, state.Pins);
    }

    // Window verbs change the durable workspace; restore.cs re-renders on the resulting WorkspaceChanged.
    public static async Task Change(IntoChatOSConnection brain, Guid request, Func<WorkspaceState, Task<WorkspaceUpdate?>> change)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = await State(brain).Read();
            if (await change(state) is not { } next) { return; }
            // Another change landed between read and apply: re-read once so the click is not dropped.
            try { await State(brain).Apply(next, "desktop:" + request, state.Revision); return; }
            catch (RevisionConflictException) when (attempt == 0) { }
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
        foreach (var (window, view) in model.Windows.Zip(Windowed(Scope(brain), state.OpenViews)))
        {
            var viewport = brain.Get<IViewport>(window.ViewportKey);
            var current = await viewport.Read();
            if (current.Target != view) { await viewport.Set(view, current.Revision); }
        }
        // Closed windows show nothing, even if an older render raced close.cs's own clear.
        var open = model.Windows.Select(window => window.ViewportKey).ToHashSet(StringComparer.Ordinal);
        await foreach (var instance in brain.Kernel.Instances<IViewport>(brain.Stopping))
        {
            if (!instance.Key.StartsWith(Key(brain) + "/window/", StringComparison.Ordinal) || open.Contains(instance.Key)) { continue; }
            var stale = brain.Get<IViewport>(instance.Key);
            var current = await stale.Read();
            if (current.Target is not null) { await stale.Set(null, current.Revision); }
        }
        if (model.Entries is { } shownEntries) { await SetConnectButtons(brain, shownEntries); }
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
        await Change(brain, Guid.NewGuid(), state => Task.FromResult<WorkspaceUpdate?>(new(
            state.OpenViews.Any(view => view.Id == "ino") ? state.OpenViews : [.. state.OpenViews, new WorkspaceViewRef("ino", ino, "full", "")], "ino", state.Pins)));
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
