using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.MarkdownViewer;

[GenerateSerializer, Alias("ui.markdown.state")]
public sealed record MarkdownViewerState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] string Markdown,
    [property: Id(3)] IReadOnlyList<string> Headings)
{
    public MarkdownViewerState() : this(0, "Document", "", []) { }
}

[NeuronAlias("ui.markdown")]
[DefaultNeuronType("ui.markdown")]
public interface IMarkdownViewer
{
    Task<MarkdownViewerState> SetDocument(string title, string markdown);
    [ReadOnlyCall] Task<MarkdownViewerState> Read();
}
