using DigitalBrain;

namespace IntoChat.OS.Modules.UI.MarkdownViewer.Signals;

[Signal("ui.markdown-changed")]
public sealed record MarkdownChanged(string ViewerId, int Version, string Title) : Signal;
