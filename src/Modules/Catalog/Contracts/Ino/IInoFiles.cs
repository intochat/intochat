using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("ino.files"), DefaultNeuronType("ino.files")]
public interface IInoFiles
{
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<InoToolResult> Find(string pattern);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<InoToolResult> Open(string path);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<InoToolResult> Summarize(string path);
}
