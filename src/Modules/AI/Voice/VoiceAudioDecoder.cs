using System.Buffers.Binary;
using Concentus;
using Concentus.Oggfile;
using Microsoft.Extensions.AI;
using NAudio.Wave;
using NLayer;

namespace IntoChat.OS.Modules.AI.Voice;

// Unified multi-format voice audio decoder (PCM16, WAV, MP3, Ogg-Opus, raw Opus) -> mono PCM16.
internal static class VoiceAudioDecoder
{
    internal const int OggOpusSampleRate = 48_000;

    internal static byte[] DecodeToMonoPcm16(DataContent data, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);

        var bytes = data.Data.ToArray();
        if (bytes.Length == 0) { return []; }

        var mediaType = NormalizeMediaType(data.MediaType);

        if (mediaType.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase))
        {
            var rate = SpeechEndpoint.ParsePcmRate(data.MediaType!);
            return rate == targetSampleRate ? bytes : Pcm16.Resample(bytes, rate, targetSampleRate);
        }

        if (mediaType is "audio/wav" or "audio/x-wav" or "audio/wave" || LooksLikeWav(bytes))
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var reader = new WaveFileReader(ms);
            var provider = reader.ToSampleProvider();
            return DecodeFloatStream(Math.Max(1, reader.WaveFormat.Channels), reader.WaveFormat.SampleRate, targetSampleRate, buf => provider.Read(buf.AsSpan()));
        }

        if (mediaType is "audio/mpeg" or "audio/mp3" || LooksLikeMp3(bytes))
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var mpeg = new MpegFile(ms);
            var rate = mpeg.SampleRate > 0 ? mpeg.SampleRate : targetSampleRate;
            return DecodeFloatStream(Math.Max(1, mpeg.Channels), rate, targetSampleRate, buf => mpeg.ReadSamples(buf, 0, buf.Length));
        }

        if (mediaType is "audio/ogg" or "audio/opus" || LooksLikeOgg(bytes))
        {
            return DecodeOpus(bytes, targetSampleRate);
        }

        throw new NotSupportedException($"Unsupported audio media type '{data.MediaType ?? "unknown"}' for voice decoding.");
    }

    internal static void DecodeOggOpus(
        Stream oggStream,
        int channels,
        Action<byte[], int> onPcmBytes,
        int sampleRate = OggOpusSampleRate)
    {
        ArgumentNullException.ThrowIfNull(oggStream);
        ArgumentNullException.ThrowIfNull(onPcmBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        using var decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
        var oggIn = new OpusOggReadStream(decoder, oggStream);

        while (oggIn.HasNextPacket)
        {
            var samples = oggIn.DecodeNextPacket();
            if (samples is not { Length: > 0 }) { continue; }

            if (channels == 1)
            {
                var bytes = new byte[samples.Length * sizeof(short)];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                onPcmBytes(bytes, bytes.Length);
                continue;
            }

            var frames = samples.Length / channels;
            var monoBytes = new byte[frames * sizeof(short)];
            for (var f = 0; f < frames; f++)
            {
                int sum = 0;
                for (var c = 0; c < channels; c++) { sum += samples[(f * channels) + c]; }
                var mono = (short)Math.Clamp(sum / channels, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(monoBytes.AsSpan(f * sizeof(short), sizeof(short)), mono);
            }

            onPcmBytes(monoBytes, monoBytes.Length);
        }
    }

    internal static int DetectOggOpusChannels(ReadOnlySpan<byte> oggBytes)
    {
        // RFC 7845 "OpusHead" identification header in the first Ogg page: byte +9 is channel count.
        var limit = Math.Min(oggBytes.Length, 512);
        var idx = oggBytes[..limit].IndexOf("OpusHead"u8);
        return idx >= 0 && idx + 9 < oggBytes.Length && oggBytes[idx + 9] is 1 or 2
            ? oggBytes[idx + 9]
            : 1;
    }

    private static string NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) { return string.Empty; }
        var semi = mediaType.IndexOf(';', StringComparison.Ordinal);
        return (semi >= 0 ? mediaType[..semi] : mediaType).Trim().ToLowerInvariant();
    }

    private static bool LooksLikeWav(ReadOnlySpan<byte> b)
        => b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WAVE"u8);

    private static bool LooksLikeOgg(ReadOnlySpan<byte> b)
        => b.Length >= 4 && b[..4].SequenceEqual("OggS"u8);

    private static bool LooksLikeMp3(ReadOnlySpan<byte> b)
        => (b.Length >= 3 && b[..3].SequenceEqual("ID3"u8))
            || (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0);

    private static byte[] DecodeFloatStream(int channels, int sourceRate, int targetRate, Func<float[], int> readChunk)
    {
        var buffer = new float[4096 * channels];
        using var outMs = new MemoryStream();
        Span<byte> pair = stackalloc byte[2];
        int read;
        while ((read = readChunk(buffer)) > 0)
        {
            var frames = read / channels;
            for (var f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (var c = 0; c < channels; c++) { sum += buffer[(f * channels) + c]; }
                var sample = (short)Math.Round(Math.Clamp(sum / channels, -1.0f, 1.0f) * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pair, sample);
                outMs.Write(pair);
            }
        }

        var pcm = outMs.ToArray();
        return sourceRate == targetRate ? pcm : Pcm16.Resample(pcm, sourceRate, targetRate);
    }

    private static byte[] DecodeOpus(byte[] opusBytes, int targetSampleRate)
    {
        if (LooksLikeOgg(opusBytes))
        {
            var channels = DetectOggOpusChannels(opusBytes);
            using var ms = new MemoryStream(opusBytes, writable: false);
            using var outMs = new MemoryStream();
            DecodeOggOpus(ms, channels, (chunk, count) => outMs.Write(chunk, 0, count));

            var pcm48k = outMs.ToArray();
            if (pcm48k.Length == 0) { return []; }
            return targetSampleRate == OggOpusSampleRate
                ? pcm48k
                : Pcm16.Resample(pcm48k, OggOpusSampleRate, targetSampleRate);
        }

        // Raw single Opus packet (RFC 6716) decoded directly at targetSampleRate mono.
        using var rawDecoder = OpusCodecFactory.CreateDecoder(targetSampleRate, 1);
        var frameSamples = new short[targetSampleRate * 120 / 1000];
        var decodedSamples = rawDecoder.Decode(opusBytes.AsSpan(), frameSamples.AsSpan(), frameSamples.Length, false);
        if (decodedSamples <= 0) { return []; }

        var rawPcm = new byte[decodedSamples * sizeof(short)];
        for (var i = 0; i < decodedSamples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(rawPcm.AsSpan(i * 2, 2), frameSamples[i]);
        }

        return rawPcm;
    }
}
