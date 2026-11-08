namespace IntoChat.OS.Modules.Flutter;

// The Presentation's pointer to a view neuron: its kind (view, viewport, webbrowser) and key. Its JSON is {"Kind","Name"}, unchanged from before the rename.
public sealed record ViewRef(
    string Kind,
    string Name);
