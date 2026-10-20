using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

// A Connect in progress: the connector, and the button neuron whose activation opens its sign-in page.
public sealed record SignIn(string ConnectorId, string Button);

// Pure composition of the apps gallery: Built-in, Mine, From others. Installed entries open,
// others Get (install for real); connections Connect (real OAuth) or say they are connected or not available.
public static class Gallery
{
    private static readonly (string Group, string Heading)[] Sections = [("builtin", "Built-in"), ("mine", "Mine"), ("others", "From others")];

    public static UiNode Render(IReadOnlyList<GalleryEntry> entries, string? notice, SignIn? signIn) => Ui.Column("gallery-panel",
    [
        Ui.Row("gallery-bar", [Ui.Text("gallery-title", "Apps"), Ui.Button("gallery-close", "Close", "gallery")], gap: 4, extents: [0, 96]),
        .. notice is { Length: > 0 } ? [Ui.Text("notice", notice)] : Array.Empty<UiNode>(),
        .. Sections.Select(section => Ui.Column("section-" + section.Group,
            [Ui.Text("heading-" + section.Group, section.Heading), .. entries.Where(entry => entry.Group == section.Group).Select(entry => Entry(entry, signIn))], gap: 4)),
    ]);

    // Keys carry group, publisher and name joined by '/', which no name contains: an original and its copy never collide.
    private static UiNode Entry(GalleryEntry entry, SignIn? signIn)
    {
        var id = entry.Group + "/" + entry.Publisher + "/" + entry.Name;
        UiNode[] action = entry.Kind == "connection" ? Connection(id, entry, signIn)
            : entry.Kind != "app" ? []
            : entry.Installed ? [Ui.Button("open-" + id, "Open", "open:" + (entry.InstalledName ?? entry.Name))]
            : [Ui.Button("get-" + id, "Get", "get:" + entry.Publisher + "/" + entry.Name)];
        return Ui.Card("entry-" + id, entry.Title, entry.Description, action);
    }

    // The sign-in button is a neuron (its activation carries {url}), so the shell opens the browser.
    private static UiNode[] Connection(string id, GalleryEntry entry, SignIn? signIn)
        => entry.Installed ? []
            : !entry.Available ? [Ui.Button("connect-" + id, "Not available", "connect:" + entry.ConnectorId, enabled: false)]
            : signIn is { } pending && pending.ConnectorId == entry.ConnectorId ? [Ui.Reference("signin-" + id, new(UIVocabulary.ButtonType, pending.Button))]
            : [Ui.Button("connect-" + id, "Connect", "connect:" + entry.ConnectorId)];
}
