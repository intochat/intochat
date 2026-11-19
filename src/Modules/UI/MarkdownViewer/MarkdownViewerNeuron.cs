using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.MarkdownViewer.Signals;

namespace IntoChat.OS.Modules.UI.MarkdownViewer;

[Neuron("ui.markdown")]
internal sealed class MarkdownViewerNeuron : Neuron<MarkdownViewerState>, IMarkdownViewer
{
    public async Task<MarkdownViewerState> SetDocument(string title, string markdown)
    {
        var body = markdown ?? "";
        var headings = body
            .Split('\n')
            .Select(static l => l.Trim())
            .Where(static l => l.StartsWith('#'))
            .Select(static l => l.TrimStart('#', ' '))
            .Where(static l => l.Length > 0)
            .Take(20)
            .ToArray();
        var next = State with
        {
            Version = State.Version + 1,
            Title = string.IsNullOrWhiteSpace(title) ? "Document" : title.Trim(),
            Markdown = body,
            Headings = headings,
        };
        await Save(next, new MarkdownChanged(Id.FullKey(), next.Version, next.Title));
        return next;
    }

    public Task<MarkdownViewerState> Read() => Task.FromResult(State);
}
