using System.Runtime.CompilerServices;
using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Support.Fakes.Voice;

/// <summary>Deterministic <see cref="ISpeechToTextClient"/> fake for voice &amp; journey tests.</summary>
public sealed class FakeSpeechToTextClient(Func<string>? nextTranscript = null) : ISpeechToTextClient
{
    private readonly Func<string> _nextTranscript = nextTranscript ?? (static () => "hello ino");

    public int Calls { get; private set; }

    public Task<SpeechToTextResponse> GetTextAsync(
        Stream audioSpeechStream,
        SpeechToTextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new SpeechToTextResponse(_nextTranscript()));
    }

    public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
        Stream audioSpeechStream,
        SpeechToTextOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetTextAsync(audioSpeechStream, options, cancellationToken).ConfigureAwait(false);
        yield return new SpeechToTextResponseUpdate(response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Deterministic <see cref="ITextToSpeechClient"/> fake returning valid 24 kHz PCM16 audio.</summary>
public sealed class FakeTextToSpeechClient(byte[]? audioPcm = null) : ITextToSpeechClient
{
    private readonly byte[] _audioPcm = audioPcm ?? new byte[960];

    public List<string> SpokenTexts { get; } = [];

    public Task<TextToSpeechResponse> GetAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        SpokenTexts.Add(text);
        var content = new DataContent(_audioPcm, "audio/pcm;rate=24000");
        return Task.FromResult(new TextToSpeechResponse([content]));
    }

    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetAudioAsync(text, options, cancellationToken).ConfigureAwait(false);
        yield return new TextToSpeechResponseUpdate(response.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Deterministic <see cref="IVoiceActivityDetector"/> fake that emits an utterance on non-silent frames.</summary>
public sealed class FakeVoiceActivityDetector(bool ready = true) : IVoiceActivityDetector
{
    public bool Ready { get; set; } = ready;

    public IVoiceActivity Open(int sampleRate = 16_000) => new FakeVoiceActivity();

    private sealed class FakeVoiceActivity : IVoiceActivity
    {
        private readonly List<byte> _buffer = [];
        private bool _speaking;

        public VoiceActivity Push(ReadOnlySpan<byte> pcm16)
        {
            var hasEnergy = false;
            for (var i = 0; i + 1 < pcm16.Length; i += 2)
            {
                var sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                if (Math.Abs((int)sample) > 100)
                {
                    hasEnergy = true;
                    break;
                }
            }

            if (hasEnergy)
            {
                _speaking = true;
                _buffer.AddRange(pcm16);
                return new VoiceActivity(Speaking: true, SpeakingFor: TimeSpan.FromMilliseconds(300), Ended: []);
            }

            if (_speaking && _buffer.Count > 0)
            {
                var segment = _buffer.ToArray();
                _buffer.Clear();
                _speaking = false;
                return new VoiceActivity(Speaking: false, SpeakingFor: TimeSpan.Zero, Ended: [segment]);
            }

            return new VoiceActivity(Speaking: false, SpeakingFor: TimeSpan.Zero, Ended: []);
        }

        public void Dispose() => _buffer.Clear();
    }
}
