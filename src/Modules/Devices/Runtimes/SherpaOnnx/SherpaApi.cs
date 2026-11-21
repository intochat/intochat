using System.Text.RegularExpressions;
using SherpaOnnx;

namespace IntoChat.OS.Modules.Devices.Runtimes.SherpaOnnx;

internal sealed partial class SherpaApi : ISherpaApi
{
    // In sherpa-onnx >= 2.0.0, Kokoro phonemizes English via lexicon-us-en.txt (DataDir/espeak-ng-data
    // is deprecated and ignored). Splitting camelCase identifiers (e.g. "IntoChat" -> "Into Chat")
    // avoids OOV word skipping on brand and symbol names.
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelCaseBoundary();

    public ISherpaTts CreateTts(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = Path.Combine(modelDirectory, SherpaModels.KokoroModelFileName);
        config.Model.Kokoro.Voices = Path.Combine(modelDirectory, "voices.bin");
        config.Model.Kokoro.Tokens = Path.Combine(modelDirectory, "tokens.txt");

        var lexicons = new List<string>();
        var usLexicon = Path.Combine(modelDirectory, "lexicon-us-en.txt");
        if (File.Exists(usLexicon))
        {
            lexicons.Add(usLexicon);
        }

        var customLexicon = Path.Combine(modelDirectory, "lexicon-custom.txt");
        if (File.Exists(customLexicon))
        {
            lexicons.Add(customLexicon);
        }

        if (lexicons.Count > 0)
        {
            config.Model.Kokoro.Lexicon = string.Join(",", lexicons);
        }

        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;

        return new NativeSherpaTts(new OfflineTts(config));
    }

    public ISherpaVad CreateVad(string modelPath, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.4f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.WindowSize = 512;
        config.SampleRate = sampleRate;
        config.NumThreads = 1;
        config.Provider = "cpu";
        config.Debug = 0;

        return new NativeSherpaVad(new VoiceActivityDetector(config, bufferSizeInSeconds: 60f), sampleRate, config.SileroVad.WindowSize);
    }

    internal static string NormalizeForLexicon(string text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : CamelCaseBoundary().Replace(text, " ");

    private sealed class NativeSherpaTts(OfflineTts tts) : ISherpaTts
    {
        private readonly Lock _gate = new();
        private bool _disposed;

        public int SampleRate => tts.SampleRate;

        public float[] Synthesize(string text, float speed, int speakerId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = NormalizeForLexicon(text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return [];
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();

                // OfflineTtsGeneratedAudio defines Dispose() without implementing IDisposable.
                var audio = ct.CanBeCanceled
                    ? tts.GenerateWithCallback(normalized, speed, speakerId, new OfflineTtsCallback((_, _) => ct.IsCancellationRequested ? 0 : 1))
                    : tts.Generate(normalized, speed, speakerId);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var source = audio.Samples;
                    if (source is null || source.Length == 0)
                    {
                        return [];
                    }

                    var copy = new float[source.Length];
                    Array.Copy(source, copy, source.Length);
                    return copy;
                }
                finally
                {
                    audio.Dispose();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                tts.Dispose();
            }
        }
    }

    private sealed class NativeSherpaVad(VoiceActivityDetector vad, int sampleRate, int windowSize) : ISherpaVad
    {
        public int SampleRate => sampleRate;

        public int WindowSize => windowSize;

        public void AcceptWaveform(float[] samples) => vad.AcceptWaveform(samples);

        public bool IsSpeechDetected() => vad.IsSpeechDetected();

        public bool IsEmpty() => vad.IsEmpty();

        public float[] PopSegment()
        {
            var front = vad.Front();
            var source = front.Samples;
            var copy = source is { Length: > 0 } ? new float[source.Length] : [];
            if (copy.Length > 0)
            {
                Array.Copy(source, copy, source.Length);
            }

            vad.Pop();
            return copy;
        }

        public void Flush() => vad.Flush();

        public void Dispose() => vad.Dispose();
    }
}
