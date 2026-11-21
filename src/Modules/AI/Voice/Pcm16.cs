using System.Buffers.Binary;

namespace IntoChat.OS.Modules.AI.Voice;

internal static class Pcm16
{
    private const int HeaderBytes = 44;
    private const short Channels = 1;
    private const short BitsPerSample = 16;
    private const short BytesPerSample = BitsPerSample / 8;

    public static byte[] Resample(ReadOnlySpan<byte> pcm, int fromRate, int toRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toRate);

        if (pcm.Length == 0)
        {
            return [];
        }

        if (fromRate == toRate)
        {
            return pcm.ToArray();
        }

        var inSamples = pcm.Length / BytesPerSample;
        if (inSamples == 0)
        {
            return [];
        }

        var outSamples = (int)Math.Round((double)inSamples * toRate / fromRate);
        if (outSamples <= 0)
        {
            return [];
        }

        var output = new byte[outSamples * BytesPerSample];
        var lastIndex = inSamples - 1;

        for (var i = 0; i < outSamples; i++)
        {
            var position = (double)i * fromRate / toRate;
            var index = (int)position;

            short sample;
            if (index >= lastIndex)
            {
                sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(lastIndex * BytesPerSample, BytesPerSample));
            }
            else
            {
                var fraction = position - index;
                var s0 = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(index * BytesPerSample, BytesPerSample));
                var s1 = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice((index + 1) * BytesPerSample, BytesPerSample));
                var interpolated = (int)Math.Round(s0 + ((s1 - s0) * fraction));
                sample = (short)Math.Clamp(interpolated, short.MinValue, short.MaxValue);
            }

            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * BytesPerSample, BytesPerSample), sample);
        }

        return output;
    }

    public static byte[] Wav(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var dataBytes = pcm.Length;
        var wav = new byte[HeaderBytes + dataBytes];
        var header = wav.AsSpan(0, HeaderBytes);

        "RIFF"u8.CopyTo(header[0..4]);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], 36 + dataBytes);
        "WAVE"u8.CopyTo(header[8..12]);
        "fmt "u8.CopyTo(header[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..22], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..24], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], sampleRate * Channels * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..34], Channels * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..36], BitsPerSample);
        "data"u8.CopyTo(header[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..44], dataBytes);

        pcm.CopyTo(wav.AsSpan(HeaderBytes));
        return wav;
    }
}
