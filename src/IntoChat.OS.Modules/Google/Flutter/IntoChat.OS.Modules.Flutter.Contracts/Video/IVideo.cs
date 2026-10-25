using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Video;

[NeuronAlias("video"), DefaultNeuronType(UIVocabulary.VideoType)]
public interface IVideo
{
    Task Load(string url, double duration);
    Task Play();
    Task Pause();
    Task Seek(double seconds);
    Task End();
    [DigitalBrain.ReadOnlyCall] Task<VideoState> Read();
}

[GenerateSerializer, Alias("ui.video-state")]
[ExportableState]
public sealed class VideoState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Url { get; set; } = "";
    [Id(3)] public bool Playing { get; set; }
    [Id(4)] public double Position { get; set; }
    [Id(5)] public double Duration { get; set; }
}
