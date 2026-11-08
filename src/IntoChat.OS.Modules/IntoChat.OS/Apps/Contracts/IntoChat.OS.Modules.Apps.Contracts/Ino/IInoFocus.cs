using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// Ino's tool for pointing the person at a component ("kind:name") or a window ("window:{appId}"). Point writes the ui.focus entry
// of the context/ui.focus context as JSON {"target","tone","reason","action":{"kind","argument"}}; the context stamps who wrote
// it. Point refuses a Critical tone from a model, and the presence fold ignores one that a model wrote anyway. The focus lapses when the next turn starts; Clear removes it.
[NeuronAlias("ino.focus"), DefaultNeuronType("ino.focus")]
public interface IInoFocus
{
    const string DefaultKey = "ino/focus";

    // The context and entry the focus lives in.
    const string ContextKey = "context/ui.focus", EntryName = "ui.focus";

    Task<InoToolResult> Point(string target, FocusTone tone, string reason, FocusAction? action = null);

    Task<InoToolResult> Clear();
}
