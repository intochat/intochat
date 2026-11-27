using System.Buffers.Binary;
using IntoChat.OS.Modules.Devices.Voice;

namespace IntoChat.OS.Modules.AI.Voice;

/// <summary>
/// Pure-C# RMS/energy <see cref="IVoiceActivityDetector"/> fallback used when Silero ONNX VAD
/// is not installed (for example, Cloud-only deployments without local ONNX binaries).
/// </summary>
internal sealed class EnergyVoiceActivityDetector(
    double speechThresholdRms = EnergyVoiceActivityDetector.DefaultSpeechThresholdRms,
    TimeSpan? minSpeechDuration = null,
    TimeSpan? silenceTailDuration = null,
    TimeSpan? maxUtteranceDuration = null,
    TimeSpan? preRollDuration = null) : IVoiceActivityDetector
{
    internal const double DefaultSpeechThresholdRms = 0.015;
    internal static readonly TimeSpan DefaultMinSpeechDuration = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan DefaultSilenceTailDuration = TimeSpan.FromMilliseconds(600);
    internal static readonly TimeSpan DefaultMaxUtteranceDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultPreRollDuration = TimeSpan.FromMilliseconds(200);

    private readonly double _speechThresholdRms = speechThresholdRms > 0 ? speechThresholdRms : DefaultSpeechThresholdRms;
    private readonly TimeSpan _minSpeechDuration = minSpeechDuration ?? DefaultMinSpeechDuration;
    private readonly TimeSpan _silenceTailDuration = silenceTailDuration ?? DefaultSilenceTailDuration;
    private readonly TimeSpan _maxUtteranceDuration = maxUtteranceDuration ?? DefaultMaxUtteranceDuration;
    private readonly TimeSpan _preRollDuration = preRollDuration ?? DefaultPreRollDuration;

    public bool Ready => true;

    public IVoiceActivity Open(int sampleRate = 16_000)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        return new Session(
            sampleRate,
            _speechThresholdRms,
            _minSpeechDuration,
            _silenceTailDuration,
            _maxUtteranceDuration,
            _preRollDuration);
    }

    internal static double ComputeRms(ReadOnlySpan<byte> pcm16)
    {
        var sampleCount = pcm16.Length / 2;
        if (sampleCount == 0)
        {
            return 0;
        }

        double sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2));
            var normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        return Math.Sqrt(sumSquares / sampleCount);
    }

    private sealed class Session : IVoiceActivity
    {
        private readonly int _sampleRate;
        private readonly double _threshold;
        private readonly int _minSpeechSamples;
        private readonly int _silenceTailSamples;
        private readonly int _maxUtteranceSamples;
        private readonly int _preRollBytes;

        private readonly List<byte> _preRoll = [];
        private readonly MemoryStream _utterance = new();

        private bool _inSpeech;
        private int _speechSamples;
        private int _silenceSamples;
        private int _utteranceSamples;
        private bool _disposed;

        public Session(
            int sampleRate,
            double threshold,
            TimeSpan minSpeechDuration,
            TimeSpan silenceTailDuration,
            TimeSpan maxUtteranceDuration,
            TimeSpan preRollDuration)
        {
            _sampleRate = sampleRate;
            _threshold = threshold;
            _minSpeechSamples = Math.Max(1, (int)(sampleRate * minSpeechDuration.TotalSeconds));
            _silenceTailSamples = Math.Max(1, (int)(sampleRate * silenceTailDuration.TotalSeconds));
            _maxUtteranceSamples = Math.Max(_minSpeechSamples, (int)(sampleRate * maxUtteranceDuration.TotalSeconds));
            _preRollBytes = Math.Max(0, (int)(sampleRate * preRollDuration.TotalSeconds) * 2);
        }

        public VoiceActivity Push(ReadOnlySpan<byte> pcm16)
        {
            if (_disposed || pcm16.Length < 2)
            {
                return new VoiceActivity(false, TimeSpan.Zero, []);
            }

            var alignedLength = pcm16.Length & ~1;
            var frame = pcm16[..alignedLength];
            var sampleCount = alignedLength / 2;
            var rms = ComputeRms(frame);
            var isSpeech = rms >= _threshold;

            if (!_inSpeech)
            {
                if (!isSpeech)
                {
                    AppendPreRoll(frame);
                    return new VoiceActivity(false, TimeSpan.Zero, []);
                }

                _inSpeech = true;
                _speechSamples = sampleCount;
                _silenceSamples = 0;
                _utteranceSamples = sampleCount;
                _utterance.SetLength(0);

                if (_preRoll.Count > 0)
                {
                    var pre = _preRoll.ToArray();
                    _utterance.Write(pre, 0, pre.Length);
                    _utteranceSamples += pre.Length / 2;
                    _preRoll.Clear();
                }

                _utterance.Write(frame);
                return new VoiceActivity(true, SamplesToDuration(_speechSamples), []);
            }

            _utterance.Write(frame);
            _utteranceSamples += sampleCount;

            if (isSpeech)
            {
                _speechSamples += sampleCount;
                _silenceSamples = 0;

                if (_utteranceSamples >= _maxUtteranceSamples)
                {
                    return CompleteUtterance();
                }

                return new VoiceActivity(true, SamplesToDuration(_speechSamples), []);
            }

            _silenceSamples += sampleCount;
            if (_silenceSamples >= _silenceTailSamples || _utteranceSamples >= _maxUtteranceSamples)
            {
                return CompleteUtterance();
            }

            return new VoiceActivity(true, SamplesToDuration(_speechSamples), []);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _preRoll.Clear();
            _utterance.Dispose();
        }

        private VoiceActivity CompleteUtterance()
        {
            ReadOnlyMemory<byte>[] ended = _speechSamples >= _minSpeechSamples && _utterance.Length > 0
                ? [_utterance.ToArray()]
                : [];

            _inSpeech = false;
            _speechSamples = 0;
            _silenceSamples = 0;
            _utteranceSamples = 0;
            _utterance.SetLength(0);
            _preRoll.Clear();

            return new VoiceActivity(false, TimeSpan.Zero, ended);
        }

        private void AppendPreRoll(ReadOnlySpan<byte> frame)
        {
            if (_preRollBytes <= 0)
            {
                return;
            }

            if (frame.Length >= _preRollBytes)
            {
                _preRoll.Clear();
                _preRoll.AddRange(frame[^_preRollBytes..]);
                return;
            }

            var overflow = _preRoll.Count + frame.Length - _preRollBytes;
            if (overflow > 0)
            {
                _preRoll.RemoveRange(0, overflow);
            }

            _preRoll.AddRange(frame);
        }

        private TimeSpan SamplesToDuration(int samples)
            => TimeSpan.FromSeconds((double)samples / _sampleRate);
    }
}
