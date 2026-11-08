namespace IntoChat.OS.Modules.Flutter;

// The Presentation's pointer to a view neuron: its kind (view, viewport, webbrowser) and key. Same JSON as UiChildRef.
public sealed record ViewRef(
    string Kind,
    string Name);
