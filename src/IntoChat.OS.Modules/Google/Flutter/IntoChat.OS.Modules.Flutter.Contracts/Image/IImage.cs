using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Image;

[NeuronAlias("image"), DefaultNeuronType(UIVocabulary.ImageType)]
public interface IImage
{
    Task Set(string url, string mediaType, string prompt);
    [DigitalBrain.ReadOnlyCall] Task<ImageState> Read();
}

[GenerateSerializer, Alias("ui.image-state")]
[ExportableState]
public sealed class ImageState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Prompt { get; set; } = "";
    [Id(3)] public string MediaType { get; set; } = "";
    [Id(4)] public string Url { get; set; } = "";
}
