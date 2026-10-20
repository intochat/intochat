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
        if (m.Windows.Count == 0) { return Ui.Column("desktop", [dock]); }
        var focus = m.FocusedId is { } id && m.Windows.Any(window => window.Id == id) ? id : m.Windows[^1].Id;
        var shown = m.Narrow ? m.Windows.Where(window => window.Id == focus).ToArray() : [.. m.Windows];
        var rows = shown.Chunk(PerRow).Select((row, i) => Ui.Row("windows-" + i, [.. row.Select(Window)])).ToArray();
        return Ui.Column("desktop", [Ui.Column("windows", rows), dock], extents: [0, 68]);
    }

    private static UiNode Window(WindowModel window) => Ui.Card("window-" + window.Id, window.Title, children:
    [
        Ui.Row("chrome-" + window.Id,
        [
            Ui.Button("focus-" + window.Id, "Focus", "focus:" + window.Id),
            Ui.Button("close-" + window.Id, "Close", "close:" + window.Id),
        ], gap: 4),
        Ui.Reference("content-" + window.Id, new(UIVocabulary.ViewportType, window.ViewportKey)),
    ]);

    private static UiNode Dock(DesktopModel m) => Ui.Row("dock",
    [
        Ui.Row("apps", [.. m.Dock.Select(item => Ui.Button("dock-" + item.Key, item.Title, "open:" + item.Key))], gap: 4),
        Ui.Button("narrow", m.Narrow ? "Side by side" : "One window", "narrow:" + (m.Narrow ? "false" : "true")),
        Ui.Button("more", "More", "more"),
    ], extents: [0, 120, 96]);
}
