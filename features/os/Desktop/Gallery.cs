using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

// Pure composition of the apps gallery: Built-in, Mine, From others. Installed entries open,
// others Get (install for real). Connections are left to their own Connect action.
public static class Gallery
{
    private static readonly (string Group, string Heading)[] Sections = [("builtin", "Built-in"), ("mine", "Mine"), ("others", "From others")];

    public static UiNode Render(IReadOnlyList<GalleryEntry> entries, string? notice) => Ui.Column("gallery-panel",
    [
        Ui.Row("gallery-bar", [Ui.Text("gallery-title", "Apps"), Ui.Button("gallery-close", "Close", "gallery")], gap: 4, extents: [0, 96]),
        .. notice is { Length: > 0 } ? [Ui.Text("notice", notice)] : Array.Empty<UiNode>(),
        .. Sections.Select(section => Ui.Column("section-" + section.Group,
            [Ui.Text("heading-" + section.Group, section.Heading), .. entries.Where(entry => entry.Group == section.Group).Select(Entry)], gap: 4)
            with { Text = section.Heading }),
    ]);

    // Keys carry publisher and name, so an original and its copy never collide.
    private static UiNode Entry(GalleryEntry entry)
    {
        var id = entry.Group + "-" + entry.Publisher + "-" + entry.Name;
        UiNode[] action = entry.Kind != "app" ? []
            : entry.Installed ? [Ui.Button("open-" + id, "Open", "open:" + (entry.InstalledName ?? entry.Name))]
            : [Ui.Button("get-" + id, "Get", "get:" + entry.Publisher + "/" + entry.Name)];
        return Ui.Card("entry-" + id, entry.Title, entry.Description, action);
    }
}
