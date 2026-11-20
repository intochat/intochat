using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.MediaPlayer.Signals;

namespace IntoChat.OS.Modules.UI.MediaPlayer;

[Neuron("ui.media-player")]
internal sealed class MediaPlayerNeuron : UiNeuronBase<MediaPlayerState>, IMediaPlayer
{
    public async Task<MediaPlayerState> Configure(string title, string artist, string sourceUrl, string kind = "audio", double durationSeconds = 180)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "Track",
            Artist = artist ?? "",
            SourceUrl = sourceUrl ?? "",
            Kind = kind is "video" or "audio" ? kind : "audio",
            DurationSeconds = Math.Max(1, durationSeconds),
            PositionSeconds = 0,
            Playing = false,
        };
        await Save(next, new MediaPlayerChanged(Id.FullKey(), next.Version, next.Playing, next.PositionSeconds));
        return next;
    }

    public async Task<MediaPlayerState> Play()
    {
        if (State.Playing) { return State; }
        var next = State with { Version = State.Version + 1, Playing = true };
        await Save(next, new MediaPlayerChanged(Id.FullKey(), next.Version, next.Playing, next.PositionSeconds));
        return next;
    }

    public async Task<MediaPlayerState> Pause()
    {
        if (!State.Playing) { return State; }
        var next = State with { Version = State.Version + 1, Playing = false };
        await Save(next, new MediaPlayerChanged(Id.FullKey(), next.Version, next.Playing, next.PositionSeconds));
        return next;
    }

    public async Task<MediaPlayerState> Seek(double positionSeconds)
    {
        var clamped = Math.Clamp(positionSeconds, 0, Math.Max(1, State.DurationSeconds));
        var next = State with { Version = State.Version + 1, PositionSeconds = clamped };
        await Save(next, new MediaPlayerChanged(Id.FullKey(), next.Version, next.Playing, next.PositionSeconds));
        return next;
    }

    public async Task<MediaPlayerState> SetVolume(double volume, bool muted = false)
    {
        var clamped = Math.Round(Math.Clamp(volume, 0, 1), 2);
        var next = State with { Version = State.Version + 1, Volume = clamped, Muted = muted };
        await Save(next, new MediaPlayerChanged(Id.FullKey(), next.Version, next.Playing, next.PositionSeconds));
        return next;
    }

    public Task<MediaPlayerState> Read() => Task.FromResult(State);
}
