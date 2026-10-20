using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

// Pure composition: windows side by side above the dock. Theme comes from the UI kit.
public static class DesktopLayout
{
    // Many windows wrap into rows of this many, so six or more never overflow one row.
    public const int PerRow = 3;

    public static UiNode Render(DesktopModel m)
    {
        var dock = Dock(m);
        // The gallery sits over the windows, above the dock, until `gallery` closes it.
        if (m.ShowGallery) { return Ui.Column("desktop", [Gallery.Render(m.Entries ?? [], m.Notice, m.SignIn), dock], extents: [0, 68]); }
        if (m.Windows.Count == 0) { return Ui.Column("desktop", [dock]); }
        var focus = m.FocusedId is { } id && m.Windows.Any(window => window.Id == id) ? id : m.Windows[^1].Id;
        var shown = m.Narrow ? m.Windows.Where(window => window.Id == focus).ToArray() : [.. m.Windows];
        var rows = shown.Chunk(PerRow).Select((row, i) => Ui.Row("windows-" + i, [.. row.Select(window => Window(window, m))])).ToArray();
        return Ui.Column("desktop", [Ui.Column("windows", rows), dock], extents: [0, 68]);
    }

    private static UiNode Window(WindowModel window, DesktopModel m) => Ui.Card("window-" + window.Id, window.Title, children:
    [
        Ui.Row("chrome-" + window.Id, [Ui.Button("focus-" + window.Id, "Focus", "focus:" + window.Id), .. Pin(window, m),
            Ui.Button("close-" + window.Id, "Close", "close:" + window.Id)], gap: 4),
        Ui.Reference("content-" + window.Id, new(UIVocabulary.ViewportType, window.ViewportKey)),
    ]);

    // Ino is always first in the dock, so its window offers no pin toggle.
    private static UiNode[] Pin(WindowModel window, DesktopModel m) => window.Id == "ino" ? []
        : m.Dock.Any(item => item.Key == window.Id && item.Pinned)
            ? [Ui.Button("unpin-" + window.Id, "Unpin", "unpin:" + window.Id)]
            : [Ui.Button("pin-" + window.Id, "Pin", "pin:" + window.Id)];

    // Apps on the left (Ino, pins, open apps, then the gallery button); usage, avatar and more on the right.
    private static UiNode Dock(DesktopModel m) => Ui.Row("dock",
    [
        Ui.Row("apps", [.. m.Dock.Select(item => Ui.Button("dock-" + item.Key, item.Title, "open:" + item.Key)), Ui.Button("gallery", "Apps", "gallery")], gap: 4),
        Ui.Button("narrow", m.Narrow ? "Side by side" : "One window", "narrow:" + (m.Narrow ? "false" : "true")),
        Ui.Row("right",
        [
            Ui.Text("usage", m.Usage ?? "Usage"),
            Ui.Button("avatar", m.Person ?? "You", "open:circle"),
            Ui.Row("more", [Ui.Button("more-settings", "Settings", "settings"), Ui.Button("more-classic", "Classic layout", "layout:classic")], gap: 4),
        ], gap: 4, extents: [72, 72, 0]),
    ], extents: [0, 120, 360]);
}
