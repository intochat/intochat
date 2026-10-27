using DigitalBrain;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Flutter.FileInput;

[Neuron(UIVocabulary.FileInputType)]
internal sealed class FileInputNeuron
    : UiNeuron<FileInputState>, IFileInput
{
    public Task Configure(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var next = Snapshot;
        next.Name = FullKey;
        next.Revision++;
        next.Label = label.Trim();
        return Save(next, new FileInputChanged(next.Name, next.Revision));
    }
    public async Task Capture(string name, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        if (name.Length > 255 || name.Any(char.IsControl)) { throw new ArgumentException("A filename has at most 255 printable characters.", nameof(name)); }
        if (content.Length > 32_000) { throw new ArgumentException("File text has at most 32000 characters.", nameof(content)); }
        var next = Snapshot;
        next.Name = FullKey;
        next.Revision++;
        var captured = new FileCaptured(next.Name, name, content);
        await Save(next, new FileInputChanged(next.Name, next.Revision), captured);
        await Dispatch(UiEvent.Of(captured));
    }
    public Task<FileInputState> Read() { Snapshot.Name = FullKey; return Task.FromResult(Snapshot); }
}
