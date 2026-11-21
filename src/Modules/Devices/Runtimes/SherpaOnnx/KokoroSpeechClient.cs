using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.Devices.Runtimes.SherpaOnnx;

internal sealed class KokoroSpeechClient : ITextToSpeechClient
{
    private readonly Func<ISherpaTts> _factory;
    private readonly Lock _gate = new();
    private readonly TextToSpeechClientMetadata _metadata = new("sherpa-onnx", defaultModelId: KnownModels.Kokoro82M.Id.Value);
    private ISherpaTts? _tts;
    private bool _disposed;

    public KokoroSpeechClient(Func<ISherpaTts> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<TextToSpeechResponse> GetAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();

        var speakerId = SherpaModels.ResolveSpeakerId(options?.VoiceId);
        var speed = ResolveSpeed(options?.Speed);
        var tts = EnsureTts();
        var samples = await Task.Run(() => tts.Synthesize(text, speed, speakerId, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var pcm = FloatToPcm16(samples);
        var sampleRate = tts.SampleRate > 0 ? tts.SampleRate : SherpaModels.KokoroSampleRate;

        return new TextToSpeechResponse([new DataContent(pcm, $"audio/pcm;rate={sampleRate}")])
        {
            ModelId = KnownModels.Kokoro82M.Id.Value,
        };
    }

    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();

        var speakerId = SherpaModels.ResolveSpeakerId(options?.VoiceId);
        var speed = ResolveSpeed(options?.Speed);
        var tts = EnsureTts();
        var sampleRate = tts.SampleRate > 0 ? tts.SampleRate : SherpaModels.KokoroSampleRate;

        foreach (var sentence in SplitSentences(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = await Task.Run(() => tts.Synthesize(sentence, speed, speakerId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var pcm = FloatToPcm16(samples);
            yield return new TextToSpeechResponseUpdate([new DataContent(pcm, $"audio/pcm;rate={sampleRate}")])
            {
                Kind = TextToSpeechResponseUpdateKind.AudioUpdated,
                ModelId = KnownModels.Kokoro82M.Id.Value,
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(TextToSpeechClientMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        ISherpaTts? instance;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            instance = _tts;
            _tts = null;
        }

        instance?.Dispose();
    }

    private ISherpaTts EnsureTts()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tts ??= _factory();
        }
    }

    private static float ResolveSpeed(float? speed)
        => speed is > 0f ? Math.Clamp(speed.Value, 0.5f, 2.0f) : 1.0f;

    internal static byte[] FloatToPcm16(ReadOnlySpan<float> samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), value);
        }

        return pcm;
    }

    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '.' or '!' or '?' or '\n')
            {
                var end = i + 1;
                while (end < text.Length && text[end] is '.' or '!' or '?' or '"' or '\'' or ')' or ']')
                {
                    end++;
                }

                var piece = text[start..end].Trim();
                if (piece.Length > 0)
                {
                    result.Add(piece);
                }

                start = end;
                i = end - 1;
            }
        }

        if (start < text.Length)
        {
            var tail = text[start..].Trim();
            if (tail.Length > 0)
            {
                result.Add(tail);
            }
        }

        return result;
    }
}
