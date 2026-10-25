using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.FileInput;

[NeuronAlias("fileinput"), DefaultNeuronType(UIVocabulary.FileInputType)]
public interface IFileInput
{
    Task Configure(string label);
    Task Capture(string name, string content);
    [ReadOnlyCall] Task<FileInputState> Read();
}

[GenerateSerializer, Alias("ui.fileinput-state")]
[ExportableState]
public sealed class FileInputState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public string Label { get; set; } = "";
}

[Signal("ui.file-captured")]
public sealed record FileCaptured(string Name, string FileName, string Content) : DigitalBrain.Signal;

[Signal("ui.fileinput-changed")]
public sealed record FileInputChanged(string Name, long Revision) : DigitalBrain.Signal;
