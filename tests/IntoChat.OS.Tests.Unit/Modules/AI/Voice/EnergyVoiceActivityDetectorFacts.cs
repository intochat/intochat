using System.Buffers.Binary;
using IntoChat.OS.Modules.AI.Voice;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Voice;

public sealed class EnergyVoiceActivityDetectorFacts
{
    private const int SampleRate = 16_000;

    [Fact]
    public void SilenceNeverTriggersSpeechOrUtterance()
    {
        var detector = new EnergyVoiceActivityDetector();
        Assert.True(detector.Ready);
        using var vad = detector.Open(SampleRate);

        var silence = MakeTone(amplitude: 0, durationMs: 100);
        for (var i = 0; i < 10; i++)
        {
            var activity = vad.Push(silence);
            Assert.False(activity.Speaking);
            Assert.Equal(TimeSpan.Zero, activity.SpeakingFor);
            Assert.Empty(activity.Ended);
        }
    }

    [Fact]
    public void SpeechOnsetFollowedBySilenceTailEmitsCompleteUtterance()
    {
        var detector = new EnergyVoiceActivityDetector(
            speechThresholdRms: 0.015,
            minSpeechDuration: TimeSpan.FromMilliseconds(150),
            silenceTailDuration: TimeSpan.FromMilliseconds(300),
            preRollDuration: TimeSpan.FromMilliseconds(100));
        using var vad = detector.Open(SampleRate);

        var silence = MakeTone(amplitude: 0, durationMs: 100);
        var speech = MakeTone(amplitude: 4_000, durationMs: 100);

        // Pre-roll silence.
        Assert.False(vad.Push(silence).Speaking);

        // 200ms of speech -> Speaking = true, SpeakingFor grows.
        var firstSpeech = vad.Push(speech);
        Assert.True(firstSpeech.Speaking);
        Assert.Equal(TimeSpan.FromMilliseconds(100), firstSpeech.SpeakingFor);
        Assert.Empty(firstSpeech.Ended);

        var secondSpeech = vad.Push(speech);
        Assert.True(secondSpeech.Speaking);
        Assert.Equal(TimeSpan.FromMilliseconds(200), secondSpeech.SpeakingFor);
        Assert.Empty(secondSpeech.Ended);

        // 200ms of silence -> still inside tail window.
        Assert.True(vad.Push(silence).Speaking);
        Assert.True(vad.Push(silence).Speaking);

        // 300ms of silence -> completes utterance.
        var completed = vad.Push(silence);
        Assert.False(completed.Speaking);
        var utterance = Assert.Single(completed.Ended);
        // Includes pre-roll (100ms) + speech (200ms) + silence tail (300ms) = 600ms.
        Assert.Equal(SampleRate * 2 * 600 / 1000, utterance.Length);
    }

    [Fact]
    public void ShortTransientClickBelowMinSpeechDurationIsIgnored()
    {
        var detector = new EnergyVoiceActivityDetector(
            speechThresholdRms: 0.015,
            minSpeechDuration: TimeSpan.FromMilliseconds(200),
            silenceTailDuration: TimeSpan.FromMilliseconds(100));
        using var vad = detector.Open(SampleRate);

        var click = MakeTone(amplitude: 5_000, durationMs: 50);
        var silence = MakeTone(amplitude: 0, durationMs: 150);

        Assert.True(vad.Push(click).Speaking);
        var ended = vad.Push(silence);
        Assert.False(ended.Speaking);
        Assert.Empty(ended.Ended);
    }

    private static byte[] MakeTone(short amplitude, int durationMs)
    {
        var samples = SampleRate * durationMs / 1000;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(amplitude * Math.Sin(2 * Math.PI * 440.0 * i / SampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), sample);
        }

        return pcm;
    }
}
