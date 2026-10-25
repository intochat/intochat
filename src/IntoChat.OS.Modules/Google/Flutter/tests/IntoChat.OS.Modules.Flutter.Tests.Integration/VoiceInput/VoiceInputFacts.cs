using IntoChat.OS.Modules.Flutter.VoiceInput;
using IntoChat.OS.Modules.Flutter.VoiceInput.Signals;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.VoiceInput;

public sealed class VoiceInputFacts(FlutterHost host)
{
    [Fact]
    public async Task CapturedAudioIsAnnouncedWithoutTranscribing()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var voice = brain.Get<IVoiceInput>("mic");
        await voice.Configure("Hold to talk");
        await using var captured = await brain.Observe<VoiceCaptured>(voice, ct);

        await voice.Capture([1, 2, 3], "audio/webm");

        var capture = await captured.NextAsync(ct: ct);
        Assert.Equal([1, 2, 3], capture.Audio);
        Assert.Equal("audio/webm", capture.MimeType);
        Assert.Equal(1, (await voice.Read()).Captures);
        await Assert.ThrowsAsync<ArgumentException>(() => voice.Capture([], "audio/webm"));
        await Assert.ThrowsAsync<ArgumentException>(() => voice.Capture([1], "text/plain"));
    }
}
