using IntoChat.OS;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Flutter.Text;

[Alias("text"), Orleans.Metadata.DefaultGrainType(UIVocabulary.TextType)]
public interface IText : INeuron
{
    Task Set(string markdown);
    // Replaces only the version read by the caller, preserving a later writer's text.
    Task<bool> TrySet(string markdown, int expectedVersion);
    [ReadOnly, Alias("read")] Task<TextState> Read();
}

[GenerateSerializer, Alias("ui.text-state")]
[ExportableState]
public sealed class TextState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Markdown { get; set; } = "";
}
