using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.CodeEditor;

[GenerateSerializer, Alias("ui.code-editor.state")]
public sealed record CodeEditorState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] string Language,
    [property: Id(3)] string Content,
    [property: Id(4)] bool ReadOnly,
    [property: Id(5)] bool WordWrap,
    [property: Id(6)] bool Dirty)
{
    public CodeEditorState() : this(0, "untitled.cs", "csharp", "", false, false, false) { }
}

[NeuronAlias("ui.code-editor")]
[DefaultNeuronType("ui.code-editor")]
public interface ICodeEditor
{
    Task<CodeEditorState> Configure(string title, string language, string content, bool readOnly = false, bool wordWrap = false);
    Task<CodeEditorState> SetContent(string content);
    Task<CodeEditorState> SaveContent();
    [ReadOnlyCall] Task<CodeEditorState> Read();
}
