using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

// Pure composition: windows side by side above the dock. Theme comes from the UI kit.
public static class DesktopLayout
{
    public static UiNode Render(DesktopModel m)
    {
        var dock = Dock(m);
        if (m.Windows.Count == 0) { return Ui.Column("desktop", [dock]); }
        var shown = m.Narrow ? m.Windows.Where(window => window.Id == (m.FocusedId ?? m.Windows[^1].Id)).ToArray() : [.. m.Windows];
        var windows = Ui.Row("windows", [.. shown.Select(Window)]);
        return Ui.Column("desktop", [windows, dock], extents: [0, 68]);
    }

    private static UiNode Window(WindowModel window) => Ui.Column("window-" + window.Id,
    [
        Ui.Text("title-" + window.Id, window.Title),
        Ui.Reference("content-" + window.Id, new("viewport", window.ViewportKey)),
    ], gap: 4, extents: [32, 0]);

    private static UiNode Dock(DesktopModel m) => Ui.Row("dock",
    [
        Ui.Row("apps", [.. m.Dock.Select(item => Ui.Button("dock-" + item.Key, item.Title, "open"))], gap: 4),
        Ui.Button("more", "More", "more"),
    ], extents: [0, 96]);
}
