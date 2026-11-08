using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("ino.files"), DefaultNeuronType("ino.files")]
public interface IInoFiles
{
    [DigitalBrain.ReadOnlyCall]
    Task<InoToolResult> Find(string pattern);

    [DigitalBrain.ReadOnlyCall]
    Task<InoToolResult> Open(string path);

    [DigitalBrain.ReadOnlyCall]
    Task<InoToolResult> Summarize(string path);
}
