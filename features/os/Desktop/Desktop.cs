using System.Globalization;
using IntoChat.OS;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

public sealed record WindowModel(string Id, string App, string Title, string ViewportKey);
public sealed record DockItem(string Key, string Title, string? Icon, bool Pinned, bool Open);
public sealed record DesktopModel(IReadOnlyList<WindowModel> Windows, string? FocusedId, IReadOnlyList<DockItem> Dock, bool Narrow, bool ShowGallery, string? Notice);

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
        var dock = installed.OrderBy(pair => pair.Key == "ino" ? 0 : pinned.Contains(pair.Key) ? 1 : 2)
            .ThenBy(pair => pair.Value.Manifest.Title, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new DockItem(pair.Key, pair.Value.Manifest.Title, pair.Value.Manifest.Icon,
                pair.Key == "ino" || pinned.Contains(pair.Key), windows.Any(window => window.Id == pair.Key))).ToArray();
        var narrow = (await Narrow(brain).Read()).Markdown == "true";
        return new(windows, state.FocusedViewId, dock, narrow, false, null);
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
        // A fresh publisher per render continues whichever version another synapse left.
        await new ViewPublisher(brain.Get<IView>(ViewKey(brain)), Revision(brain)).Publish(DesktopLayout.Render(model));
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
