using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Video.Signals;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Video;

[GrainType(UIVocabulary.VideoType)]
internal sealed class VideoNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<VideoState> store)
    : Neuron<VideoState>(store), IVideo
{
    public Task Load(string url, double duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (duration < 0) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Url = url.Trim();
        next.Duration = duration;
        next.Position = 0;
        next.Playing = false;
        return Save(next, new VideoChanged(this.GetPrimaryKeyString(), false));
    }

    public Task Play()
    {
        var next = RequireLoaded();
        next.Version++;
        next.Playing = true;
        return Save(next, new VideoChanged(this.GetPrimaryKeyString(), true));
    }

    public Task Pause()
    {
        var next = RequireLoaded();
        next.Version++;
        next.Playing = false;
        return Save(next, new VideoChanged(this.GetPrimaryKeyString(), false));
    }

    public Task Seek(double seconds)
    {
        var next = RequireLoaded();
        next.Version++;
        next.Position = Math.Clamp(seconds, 0, next.Duration);
        return Save(next, new VideoChanged(this.GetPrimaryKeyString(), next.Playing));
    }

    public Task End()
    {
        var next = RequireLoaded();
        next.Version++;
        next.Playing = false;
        next.Position = next.Duration;
        return Save(next, new VideoChanged(this.GetPrimaryKeyString(), false), new VideoEnded(this.GetPrimaryKeyString()));
    }

    [ReadOnly] public Task<VideoState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }

    private VideoState RequireLoaded()
    {
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        if (string.IsNullOrWhiteSpace(next.Url)) { throw new InvalidOperationException("Video has no source."); }
        return next;
    }
}
