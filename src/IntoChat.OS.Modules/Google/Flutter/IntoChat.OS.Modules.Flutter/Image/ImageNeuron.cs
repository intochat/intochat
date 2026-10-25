using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Image.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Image;

[GrainType(UIVocabulary.ImageType)]
internal sealed class ImageNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ImageState> store)
    : Neuron<ImageState>(store), IImage
{
    public Task Set(string url, string mediaType, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(prompt);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Url = url.Trim();
        next.MediaType = mediaType.Trim();
        next.Prompt = prompt;
        return Save(next, new ImageChanged(this.GetPrimaryKeyString(), next.Version));
    }

    [ReadOnly] public Task<ImageState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
