using DigitalBrain;

namespace IntoChat.OS.Modules.UI.CodeEditor.Signals;

[Signal("ui.code-editor-changed")]
public sealed record CodeEditorChanged(string EditorId, int Version, bool Dirty) : Signal;

[Signal("ui.code-editor-saved")]
public sealed record CodeEditorSaved(string EditorId, int Version, string Title, string Content) : Signal;
