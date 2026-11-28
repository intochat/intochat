using IntoChat.OS.Modules.AI.Voice;
using NAudio.Wave;

namespace IntoChat.OS.Modules.AI;

// Telegram-class Ogg Opus -> PCM WAV (IAW AudioConverter port).
internal sealed class OggOpusToWavConverter : IAudioConverter
{
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    public string ConvertToWav(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var outputPath = Path.Combine(Path.GetTempPath(), $"db_voice_{Guid.NewGuid():N}.wav");

        using var fileIn = File.OpenRead(inputPath);
        using var wavWriter = new WaveFileWriter(outputPath, new WaveFormat(VoiceAudioDecoder.OggOpusSampleRate, BitsPerSample, Channels));
        VoiceAudioDecoder.DecodeOggOpus(fileIn, Channels, (bytes, count) => wavWriter.Write(bytes, 0, count));

        return outputPath;
    }
}
