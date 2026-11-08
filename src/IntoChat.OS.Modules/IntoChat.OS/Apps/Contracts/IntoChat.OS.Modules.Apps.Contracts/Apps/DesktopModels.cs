namespace IntoChat.OS.Apps.Desktop;

// The Desktop app's persisted state. It lives beside the app contracts (as InoAppState does) so the host that composes the
// desktop's window shell reads its widgets; the namespace is the app's own, so the stored state is unchanged.
public sealed record DesktopAppState(
    string LayoutMode,
    bool IsNarrow,
    string StylePreset,
    string BarLayout,
    int ActiveNeurons,
    string Status,
    string[] PinnedApps)
{
    public DesktopAppState() : this("smart-desktop", false, "fluent-acrylic", "floating-glass", 10, "Smart Desktop Ready", ["ino", "creator"]) { }

    // The desktop's own records (added fields; absent in state saved before them). The open widgets, in the order opened, and
    // whether Ino has greeted the person (once per brain). They lived in text neurons before; those are not migrated.
    public string[] Widgets { get; init; } = [];

    public bool Greeted { get; init; }
}
