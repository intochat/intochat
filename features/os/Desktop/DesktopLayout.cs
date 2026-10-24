using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

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
        // The gallery sits over the windows, above the dock, until `gallery` closes it.
        if (m.ShowGallery) { return Desktop(Gallery.Render(m.Entries ?? [], m.Notice, m.ConnectButtons), dock, m); }
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

    // Apps on the left (Ino, favourites, pins and open apps, New tab, then the gallery); usage, avatar and more on the right.
    private static UiNode Dock(DesktopModel m) => Ui.Row("dock",
    [
        Ui.Row("apps",
        [
            .. m.Dock.Where(item => item.Key == "ino").Select(item => Ui.Button("dock-ino", item.Title, "open:ino", style: UiStyle.Primary, icon: "ino")),
            .. Favourites(m),
            .. m.Dock.Where(item => item.Key != "ino").Select(item => Ui.Button("dock-" + item.Key, item.Title, "open:" + item.Key, style: UiStyle.Ghost)),
            Ui.IconButton("newtab", "plus", "New tab", "newtab"),
            Ui.Button("gallery", "Apps", "gallery", style: UiStyle.Ghost, icon: "grid"),
        ], gap: 4),
        Ui.Row("right",
        [
            Ui.Text("usage", m.Usage ?? "Usage"),
            Ui.Button("avatar", m.Person ?? "You", "open:circle"),
            Ui.Row("more", [m.SettingsButton is { } settings
                ? Ui.Reference("more-settings", new(UIVocabulary.ButtonType, settings)) : Ui.Button("more-settings", "Settings", "settings", enabled: false), m.LayoutButton is { } classic
                ? Ui.Reference("more-classic", new(UIVocabulary.ButtonType, classic)) : Ui.Button("more-classic", "Classic layout", "layout", enabled: false)], gap: 4),
        ], gap: 4, extents: [72, 72, 0]),
    ], extents: [0, 360]);

    // Hidden when empty; a menu when narrow.
    private static UiNode[] Favourites(DesktopModel m)
    {
        if (m.Favourites is not { Count: > 0 } favourites) { return []; }
        UiNode[] items = [.. favourites.Select((favourite, i) => Ui.Button("fav-" + i, favourite.Title, favourite.Action, style: UiStyle.Ghost, icon: favourite.View ? "list" : "grid"))];
        return [m.Narrow ? Ui.Menu("favourites", "Favourites", items, icon: "star") : Ui.Row("favourites", items, gap: 4)];
    }
}
