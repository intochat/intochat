using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.MediaPlayer;

[GenerateSerializer, Alias("ui.media-player.state")]
public sealed record MediaPlayerState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] string Artist,
    [property: Id(3)] string SourceUrl,
    [property: Id(4)] string Kind,
    [property: Id(5)] bool Playing,
    [property: Id(6)] double PositionSeconds,
    [property: Id(7)] double DurationSeconds,
    [property: Id(8)] double Volume,
    [property: Id(9)] bool Muted)
{
    public MediaPlayerState() : this(0, "Track", "IntoChat OS", "", "audio", false, 0, 180, 0.8, false) { }
}

[NeuronAlias("ui.media-player")]
[DefaultNeuronType("ui.media-player")]
public interface IMediaPlayer : IUiInteractiveControl
{
    Task<MediaPlayerState> Configure(string title, string artist, string sourceUrl, string kind = "audio", double durationSeconds = 180);
    Task<MediaPlayerState> Play();
    Task<MediaPlayerState> Pause();
    Task<MediaPlayerState> Seek(double positionSeconds);
    Task<MediaPlayerState> SetVolume(double volume, bool muted = false);
    [ReadOnlyCall] Task<MediaPlayerState> Read();
}
