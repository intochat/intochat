using System.Buffers.Binary;
using IntoChat.OS.Modules.Devices.Voice;

namespace IntoChat.OS.Modules.Devices.Runtimes.SherpaOnnx;

internal sealed class SileroVoiceActivity : IVoiceActivity
{
    private readonly ISherpaVad _vad;
    private readonly int _sampleRate;
    private readonly int _windowSize;
    private readonly List<float> _pending = [];
    private TimeSpan _speakingFor;
    private bool _disposed;

    public SileroVoiceActivity(ISherpaVad vad)
    {
        ArgumentNullException.ThrowIfNull(vad);
        _vad = vad;
        _sampleRate = vad.SampleRate > 0 ? vad.SampleRate : 16_000;
        _windowSize = vad.WindowSize > 0 ? vad.WindowSize : 512;
    }

    public VoiceActivity Push(ReadOnlySpan<byte> pcm16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sampleCount = pcm16.Length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2));
            _pending.Add(sample / 32768f);
        }

        var speaking = _vad.IsSpeechDetected();
        var windowDuration = TimeSpan.FromSeconds((double)_windowSize / _sampleRate);
        var offset = 0;
        var window = new float[_windowSize];

        while (_pending.Count - offset >= _windowSize)
        {
            _pending.CopyTo(offset, window, 0, _windowSize);
            offset += _windowSize;

            _vad.AcceptWaveform(window);
            speaking = _vad.IsSpeechDetected();
            _speakingFor = speaking ? _speakingFor + windowDuration : TimeSpan.Zero;
        }

        if (offset > 0)
        {
            _pending.RemoveRange(0, offset);
        }

        List<ReadOnlyMemory<byte>>? ended = null;
        while (!_vad.IsEmpty())
        {
            var segment = _vad.PopSegment();
            if (segment.Length == 0)
            {
                continue;
            }

            ended ??= [];
            ended.Add(KokoroSpeechClient.FloatToPcm16(segment));
        }

        return new VoiceActivity(
            Speaking: speaking,
            SpeakingFor: speaking ? _speakingFor : TimeSpan.Zero,
            Ended: ended is null ? [] : [.. ended]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vad.Dispose();
    }
}
