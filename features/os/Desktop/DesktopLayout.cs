using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;

// Pure composition: windows side by side above the dock. Theme comes from the UI kit.
public static class DesktopLayout
{
    // Many windows wrap into rows of this many, so six or more never overflow one row.
    public const int PerRow = 3;
    // A window's header (title and icon buttons) and its tab strip; the active tab's view fills the rest.
    public const double HeaderHeight = 40;
    public const double TabStripHeight = 38;
    public const double ChromeHeight = HeaderHeight + TabStripHeight;

    public static UiNode Render(DesktopModel m)
    {
        var dock = Dock(m);
        // An empty desktop keeps the (empty) window area, so the dock stays at the bottom.
        if (m.Windows.Count == 0) { return Desktop(Ui.Column("windows", []), dock, m); }
        var focus = m.FocusedId is { } id && m.Windows.Any(window => window.Id == id) ? id : m.Windows[^1].Id;
        var shown = m.Narrow ? m.Windows.Where(window => window.Id == focus).ToArray() : [.. m.Windows];
        var rows = shown.Chunk(PerRow).Select((row, i) => Ui.Row("windows-" + i, [.. row.Select(window => Window(window, m))])).ToArray();
        // Rows share the window area, so every window's view gets a bounded height.
        return Desktop(Ui.Column("windows", rows, extents: [.. rows.Select(_ => 0.0)]), dock, m);
    }

    // A refusal toast sits between the windows and the dock until the next change succeeds.
    private static UiNode Desktop(UiNode content, UiNode dock, DesktopModel m) => m.Toast is { } toast
        ? Ui.Column("desktop", [content, Ui.Toast("toast", toast, UiTone.Error), dock], extents: [0, 48, 68])
        : Ui.Column("desktop", [content, dock], extents: [0, 68]);

    private static UiNode Window(WindowModel window, DesktopModel m) => Ui.Column("frame-" + window.Id,
    [
        Header(window, m),
        // Tab keys use the index, so they never collide whatever the windows and apps are called.
        Ui.Tabs("tabs-" + window.Id, [.. window.Tabs.Select((tab, i) => Ui.Tab("tab-" + window.Id + "-" + i, tab.Title, "tab:" + window.Id + "/" + i, tab.Id == window.ActiveTabId))]),
        Ui.Reference("content-" + window.Id, new(UIVocabulary.ViewportType, window.ViewportKey)),
    ], gap: 0, extents: [HeaderHeight, TabStripHeight, 0]);

    private static UiNode Header(WindowModel window, DesktopModel m)
    {
        var active = Math.Max(0, window.Tabs.ToList().FindIndex(tab => tab.Id == window.ActiveTabId));
        var favourite = window.Tabs.Count > 0 && window.Tabs[active].Favourite;
        UiNode[] buttons =
        [
            Ui.Button("focus-" + window.Id, window.Title, "focus:" + window.Id, style: UiStyle.Ghost),
            Ui.IconButton("newtab-" + window.Id, "plus", "New tab", "newtab:" + window.Id),
            Ui.IconButton("star-" + window.Id, "star", favourite ? "Remove from favourites" : "Add to favourites", "favourite:" + window.Id),
            .. Pin(window, m),
            Ui.IconButton("close-" + window.Id, "x", "Close tab", "close:" + window.Id + "/" + active),
        ];
        return Ui.Row("chrome-" + window.Id, buttons, gap: 4, extents: [0, .. buttons.Skip(1).Select(_ => 32.0)]);
    }

    // Ino is always first in the dock, so a window showing Ino offers no pin toggle.
    private static UiNode[] Pin(WindowModel window, DesktopModel m) => window.ActiveApp == "ino" ? []
        : m.Dock.Any(item => item.Key == window.ActiveApp && item.Pinned)
            ? [Ui.IconButton("unpin-" + window.Id, "pin-off", "Unpin", "unpin:" + window.ActiveApp)]
            : [Ui.IconButton("pin-" + window.Id, "pin", "Pin", "pin:" + window.ActiveApp)];

    // Apps on the left (Ino, favourites, pins and open apps, New tab, then Apps); usage, avatar and more on the right.
    // Narrow folds the right group into one More menu; Settings and Classic stay button neurons there, so their
    // activations still reach the shell.
    private static UiNode Dock(DesktopModel m)
    {
        var apps = Packed("apps",
        [
            .. m.Dock.Where(item => item.Key == "ino").Select(item => (Ui.Button("dock-ino", item.Title, "open:ino", style: UiStyle.Primary, icon: "ino"), Width(item.Title, true))),
            .. Favourites(m),
            .. m.Dock.Where(item => item.Key != "ino").Select(item => (Ui.Button("dock-" + item.Key, item.Title, "open:" + item.Key, style: UiStyle.Ghost), Width(item.Title, false))),
            (Ui.IconButton("newtab", "plus", "New tab", "newtab"), IconWidth),
            // The Apps app's window: its own key, since the row is "apps" and an open Apps app is "dock-apps".
            (Ui.Button("apps-open", "Apps", "open:apps", style: UiStyle.Ghost, icon: "grid"), IconWidth),
        ]);
        UiNode settings = m.SettingsButton is { } armed
            ? Ui.Reference("more-settings", new(UIVocabulary.ButtonType, armed)) : Ui.Button("more-settings", "Settings", "settings", enabled: false);
        UiNode classic = m.LayoutButton is { } layout
            ? Ui.Reference("more-classic", new(UIVocabulary.ButtonType, layout)) : Ui.Button("more-classic", "Classic layout", "layout", enabled: false);
        var usage = Ui.Text("usage", m.Usage ?? "Usage");
        var avatar = Ui.Button("avatar", m.Person ?? "You", "open:circle");
        if (m.Narrow) { return Ui.Row("dock", [apps, Ui.Menu("more", "More", [usage, avatar, settings, classic], icon: "more-horizontal")], extents: [0, IconWidth]); }
        return Ui.Row("dock",
        [
            apps,
            Ui.Row("right", [usage, avatar, Ui.Row("more", [settings, classic], gap: 4)], gap: 4, extents: [72, 72, 0]),
        ], extents: [0, 360]);
    }

    // A row's children share its width equally unless given extents: each item gets a slot sized from its label, so
    // items pack left like a dock. No flexible spacer: the renderer reserves 96 px for each flexible child, which at
    // 390 px would push the row into scrolling; with fixed slots only, the leftover width simply stays empty.
    private const double Gap = 4;
    private const double IconWidth = 40;
    private static UiNode Packed(string key, (UiNode Node, double Width)[] items)
        => Ui.Row(key, [.. items.Select(item => item.Node)], gap: Gap, extents: [.. items.Select(item => item.Width)]);

    // The kit's 48 px button: 18 px padding each side, 14 px per character of 14 px bold text (13 still wraps "Report" in the
    // bundled font), and an icon with its gap.
    public static double Width(string label, bool icon) => 36 + (14.0 * label.Length) + (icon ? 28 : 0);

    // Hidden when empty; a menu when narrow. Each favourite shows its name beside its icon.
    private static (UiNode, double)[] Favourites(DesktopModel m)
    {
        if (m.Favourites is not { Count: > 0 } favourites) { return []; }
        var items = favourites.Select((favourite, i) => (Node: Ui.Button("fav-" + i, favourite.Title, favourite.Action, style: UiStyle.Secondary, icon: favourite.View ? "list" : "grid"),
            Width: Width(favourite.Title, true))).ToArray();
        if (m.Narrow) { return [(Ui.Menu("favourites", "Favourites", [.. items.Select(item => item.Node)], icon: "star"), IconWidth)]; }
        return [(Ui.Row("favourites", [.. items.Select(item => item.Node)], gap: Gap, extents: [.. items.Select(item => item.Width)]),
            items.Sum(item => item.Width) + Gap * (items.Length - 1))];
    }
}
