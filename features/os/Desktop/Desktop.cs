using System.Globalization;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Flutter.Workspace;

public sealed record WindowModel(string Id, string App, string Title, string ViewportKey);
public sealed record DockItem(string Key, string Title, string? Icon, bool Pinned, bool Open);
public sealed record DesktopModel(IReadOnlyList<WindowModel> Windows, string? FocusedId, IReadOnlyList<DockItem> Dock, bool Narrow, bool ShowGallery, string? Notice);

// Shared by the desktop's synapses: keys, state, and composing the model from the brain.
public static class Desktop
{
    // Background services and roots never appear in the dock.
    private static readonly HashSet<string> Hidden = ["desktop", "workspace", "router", "selfimprove", "circleroom", "usage"];

    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string ViewKey(IntoChatOSConnection brain) => Key(brain) + "/view";
    public static string Scope(IntoChatOSConnection brain) => Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static IWorkspace State(IntoChatOSConnection brain) => brain.Get<IWorkspace>(Scope(brain));
    public static long Revision(IntoChatOSConnection brain) => long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture);

    public static async Task<DesktopModel> Model(IntoChatOSConnection brain)
    {
        var state = await State(brain).Read();
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
        var windows = state.OpenViews.Select(view => new WindowModel(view.Id, view.App,
            Title(IApp.NameOf(Scope(brain), view.App) ?? view.Id), Key(brain) + "/window-" + view.Id)).ToArray();
        var pinned = state.Pins.Select(view => view.Id).ToHashSet(StringComparer.Ordinal);
        var dock = installed.OrderBy(pair => pair.Key == "ino" ? 0 : pinned.Contains(pair.Key) ? 1 : 2)
            .ThenBy(pair => pair.Value.Manifest.Title, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new DockItem(pair.Key, pair.Value.Manifest.Title, pair.Value.Manifest.Icon,
                pair.Key == "ino" || pinned.Contains(pair.Key), windows.Any(window => window.Id == pair.Key))).ToArray();
        return new(windows, state.FocusedViewId, dock, false, false, null);
    }

    // Each window is a viewport onto its app's view; the desktop tree only references it.
    public static async Task Start(IntoChatOSConnection brain, ViewPublisher publisher)
    {
        var model = await Model(brain);
        var state = await State(brain).Read();
        foreach (var window in model.Windows)
        {
            var viewport = brain.Get<IViewport>(window.ViewportKey);
            await viewport.Set(state.OpenViews.First(view => view.Id == window.Id), (await viewport.Read()).Revision);
        }
        await publisher.Publish(DesktopLayout.Render(model));
        await brain.Get<IApp>(Key(brain)).Present(Revision(brain), new(new Sphere("icon.svg", []),
            Main: new(UIVocabulary.ViewType, ViewKey(brain)), Views: [new("full", new(UIVocabulary.ViewType, ViewKey(brain)))]));
    }
}
