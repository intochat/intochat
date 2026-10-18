using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Video;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Integration.Video;

public sealed class VideoFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task LoadThenPlay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var video = brain.Get<IVideo>("intro");
        await video.Load("https://example.com/a.mp4", 12);
        await video.Play();
        Assert.True((await video.Read()).Playing);
    }
}
