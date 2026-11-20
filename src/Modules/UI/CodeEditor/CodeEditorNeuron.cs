using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.CodeEditor.Signals;

namespace IntoChat.OS.Modules.UI.CodeEditor;

[Neuron("ui.code-editor")]
internal sealed class CodeEditorNeuron : UiNeuronBase<CodeEditorState>, ICodeEditor
{
    public async Task<CodeEditorState> Configure(string title, string language, string content, bool readOnly = false, bool wordWrap = false)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Title = string.IsNullOrWhiteSpace(title) ? "untitled" : title.Trim(),
            Language = string.IsNullOrWhiteSpace(language) ? "text" : language.Trim().ToLowerInvariant(),
            Content = content ?? "",
            ReadOnly = readOnly,
            WordWrap = wordWrap,
            Dirty = false,
        };
        await Save(next, new CodeEditorChanged(Id.FullKey(), next.Version, next.Dirty));
        return next;
    }

    public async Task<CodeEditorState> SetContent(string content)
    {
        if (State.ReadOnly) { return State; }
        var normalized = content ?? "";
        if (string.Equals(normalized, State.Content, StringComparison.Ordinal)) { return State; }
        var next = State with { Version = State.Version + 1, Content = normalized, Dirty = true };
        await Save(next, new CodeEditorChanged(Id.FullKey(), next.Version, next.Dirty));
        return next;
    }

    public async Task<CodeEditorState> SaveContent()
    {
        var next = State with { Version = State.Version + 1, Dirty = false };
        await Save(next, new CodeEditorChanged(Id.FullKey(), next.Version, false), new CodeEditorSaved(Id.FullKey(), next.Version, next.Title, next.Content));
        return next;
    }

    public Task<CodeEditorState> Read() => Task.FromResult(State);
}
