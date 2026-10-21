using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

// Pure composition of the apps gallery: Built-in, Mine, From others. Installed entries open,
// others Get (install for real); connections Connect (the shell's connection window, as the user) or say they are connected or not available.
public static class Gallery
{
    private static readonly (string Group, string Heading)[] Sections = [("builtin", "Built-in"), ("mine", "Mine"), ("others", "From others")];

    public static UiNode Render(IReadOnlyList<GalleryEntry> entries, string? notice, string? connectButtons) => Ui.Column("gallery-panel",
    [
        Ui.Row("gallery-bar", [Ui.Text("gallery-title", "Apps"), Ui.Button("gallery-close", "Close", "gallery")], gap: 4, extents: [0, 96]),
        .. notice is { Length: > 0 } ? [Ui.Text("notice", notice)] : Array.Empty<UiNode>(),
        .. Sections.Select(section => Ui.Column("section-" + section.Group,
            [Ui.Text("heading-" + section.Group, section.Heading), .. entries.Where(entry => entry.Group == section.Group).Select(entry => Entry(entry, connectButtons))], gap: 4)),
    ]);

    // Keys carry group, publisher and name joined by '/', which no name contains: an original and its copy never collide.
    private static UiNode Entry(GalleryEntry entry, string? connectButtons)
    {
        // Connections key by connector alone, under their own prefix, so no app listing can collide with one.
        var id = entry.Kind == "connection" ? "connection/" + entry.ConnectorId : entry.Group + "/" + entry.Publisher + "/" + entry.Name;
        UiNode[] action = entry.Kind == "connection" ? Connection(id, entry, connectButtons)
            : entry.Kind != "app" ? []
            : entry.Installed ? [Ui.Button("open-" + id, "Open", "open:" + (entry.InstalledName ?? entry.Name))]
            : [Ui.Button("get-" + id, "Get", "get:" + entry.Publisher + "/" + entry.Name)];
        return Ui.Card("entry-" + id, entry.Title, entry.Description, action);
    }

    // Connect is a button neuron per connector (Desktop.ConnectButton): its activation hands the shell
    // {kind: connections, connector}, and the shell's connection window signs in under the user's own session.
    public static string ConnectButton(string prefix, string connectorId) => prefix + "/" + connectorId;

    private static UiNode[] Connection(string id, GalleryEntry entry, string? connectButtons)
        => entry.Installed ? []
            : !entry.Available || connectButtons is null ? [Ui.Button("connect-" + id, "Not available", "connect:" + entry.ConnectorId, enabled: false)]
            : [Ui.Reference("connect-" + id, new(UIVocabulary.ButtonType, ConnectButton(connectButtons, entry.ConnectorId!)))];
}
