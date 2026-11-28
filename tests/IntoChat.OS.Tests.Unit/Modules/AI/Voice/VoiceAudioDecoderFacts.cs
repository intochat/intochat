#pragma warning disable MEAI001 // Unit tests for VoiceAudioDecoder, TalkWebSocketTransport, and TalkToolDispatcher.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Voice;

public sealed class VoiceAudioDecoderFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void DecodeToMonoPcm16HandlesEmptyAndPcmResampling()
    {
        Assert.Empty(VoiceAudioDecoder.DecodeToMonoPcm16(new DataContent(Array.Empty<byte>(), "audio/pcm;rate=24000"), 24_000));

        var pcm24 = new byte[] { 0x10, 0x00, 0x20, 0x00, 0x30, 0x00, 0x40, 0x00 };
        Assert.Equal(pcm24, VoiceAudioDecoder.DecodeToMonoPcm16(new DataContent(pcm24, "audio/pcm;rate=24000"), 24_000));

        var pcm16 = new byte[16_000 * 2];
        var resampled = VoiceAudioDecoder.DecodeToMonoPcm16(new DataContent(pcm16, "audio/pcm;rate=16000"), 24_000);
        Assert.Equal(24_000 * 2, resampled.Length);
    }

    [Fact]
    public void DecodeToMonoPcm16DecodesWavAndRawOpusPacket()
    {
        var wav16 = Pcm16.Wav(new byte[1_600 * 2], 16_000);
        var fromWav = VoiceAudioDecoder.DecodeToMonoPcm16(new DataContent(wav16, "audio/wav"), 24_000);
        Assert.Equal(2_400 * 2, fromWav.Length);

        // Encode a single 20ms RFC 6716 Opus packet at 24 kHz mono (480 samples).
        using var encoder = OpusCodecFactory.CreateEncoder(24_000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var samples = new short[480];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / 24_000) * 10_000);
        }

        var packetBuf = new byte[4000];
        var packetLen = encoder.Encode(samples.AsSpan(), samples.Length, packetBuf.AsSpan(), packetBuf.Length);
        var rawOpus = packetBuf[..packetLen];

        var decoded = VoiceAudioDecoder.DecodeToMonoPcm16(new DataContent(rawOpus, "audio/opus"), 24_000);
        Assert.Equal(480 * sizeof(short), decoded.Length);
    }

    [Fact]
    public void DecodeOggOpusAndConverterShareSamePipeline()
    {
        var oggBytes = EncodeOggOpus(channels: 1);
        Assert.Equal(1, VoiceAudioDecoder.DetectOggOpusChannels(oggBytes));

        var pcm24 = VoiceAudioDecoder.DecodeToMonoPcm16(new DataContent(oggBytes, "audio/ogg;codecs=opus"), 24_000);
        Assert.NotEmpty(pcm24);
        Assert.Equal(0, pcm24.Length % 2);

        var tempOgg = Path.Combine(Path.GetTempPath(), $"os19_test_{Guid.NewGuid():N}.ogg");
        string? wavPath = null;
        try
        {
            File.WriteAllBytes(tempOgg, oggBytes);
            var converter = new OggOpusToWavConverter();
            wavPath = converter.ConvertToWav(tempOgg);
            Assert.True(File.Exists(wavPath));

            using var reader = new WaveFileReader(wavPath);
            Assert.Equal(VoiceAudioDecoder.OggOpusSampleRate, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            Assert.True(reader.Length > 0);
        }
        finally
        {
            if (File.Exists(tempOgg)) { File.Delete(tempOgg); }
            if (wavPath is not null && File.Exists(wavPath)) { File.Delete(wavPath); }
        }
    }

    [Fact]
    public void DecodeToMonoPcm16DownmixesStereoOggOpus()
    {
        var stereoOgg = EncodeOggOpus(channels: 2);
        Assert.Equal(2, VoiceAudioDecoder.DetectOggOpusChannels(stereoOgg));

        var pcm24 = VoiceAudioDecoder.DecodeToMonoPcm16(new DataContent(stereoOgg, "audio/ogg"), 24_000);
        Assert.NotEmpty(pcm24);
        Assert.Equal(0, pcm24.Length % 2);
    }

    [Fact]
    public void TruncateUtf8RespectsMultiByteBoundaries()
    {
        Assert.Equal(string.Empty, TalkWebSocketTransport.TruncateUtf8(string.Empty, 10));
        Assert.Equal("hello", TalkWebSocketTransport.TruncateUtf8("hello", 10));

        // Each '€' is 3 bytes in UTF-8; max 5 bytes must yield 1 character (3 bytes), never a partial sequence.
        var truncated = TalkWebSocketTransport.TruncateUtf8("€€€", 5);
        Assert.Equal("€", truncated);
        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= 5);
    }

    [Fact]
    public void ExtractToolRequestPrefersRequestKeyThenFirstArgThenFallback()
    {
        using var jsonDoc = JsonDocument.Parse("\"from json\"");
        var explicitReq = new FunctionCallContent("c1", TalkToolDispatcher.BrainTurnToolName,
            new Dictionary<string, object?> { ["other"] = "ignored", ["request"] = jsonDoc.RootElement });
        Assert.Equal("from json", TalkToolDispatcher.ExtractToolRequest(explicitReq, "fallback"));

        var firstArg = new FunctionCallContent("c2", TalkToolDispatcher.BrainTurnToolName,
            new Dictionary<string, object?> { ["query"] = "  check files  " });
        Assert.Equal("check files", TalkToolDispatcher.ExtractToolRequest(firstArg, "fallback"));

        var emptyArgs = new FunctionCallContent("c3", TalkToolDispatcher.BrainTurnToolName, new Dictionary<string, object?>());
        Assert.Equal("fallback", TalkToolDispatcher.ExtractToolRequest(emptyArgs, "fallback"));
    }

    [Fact]
    public async Task ToolDispatcherEnforcesDeduplicationRateLimitAndUnknownToolRejection()
    {
        var sentEvents = new ConcurrentQueue<TalkEvent>();
        var session = new RecordingRealtimeSession();
        var brainCalls = 0;

        var dispatcher = new TalkToolDispatcher(
            (ev, _) => { sentEvents.Enqueue(ev); return Task.CompletedTask; },
            session,
            (req, _, _) =>
            {
                Interlocked.Increment(ref brainCalls);
                return Task.FromResult($"ok:{req}");
            },
            NullLogger.Instance,
            maxBrainTurnCalls: 1);

        var delegated = 0;
        var calls = new[]
        {
            new FunctionCallContent("dup_1", TalkToolDispatcher.BrainTurnToolName, new Dictionary<string, object?> { ["request"] = "first" }),
            new FunctionCallContent("dup_1", TalkToolDispatcher.BrainTurnToolName, new Dictionary<string, object?> { ["request"] = "duplicate" }),
            new FunctionCallContent("call_2", TalkToolDispatcher.BrainTurnToolName, new Dictionary<string, object?> { ["request"] = "over limit" }),
            new FunctionCallContent("call_3", "unknown_tool", new Dictionary<string, object?>()),
        };

        var executed = await dispatcher.ExecuteCallsAsync(calls, () => "heard", () => Interlocked.Increment(ref delegated), Ct);
        Assert.True(executed);
        Assert.Equal(1, brainCalls);
        Assert.Equal(1, delegated);

        var results = session.SentMessages
            .OfType<CreateConversationItemRealtimeClientMessage>()
            .Select(static m => Assert.Single(m.Item!.Contents.OfType<FunctionResultContent>()))
            .ToArray();

        Assert.Equal(3, results.Length);
        Assert.Equal("dup_1", results[0].CallId);
        Assert.Equal("ok:first", results[0].Result);
        Assert.Equal("call_2", results[1].CallId);
        Assert.Contains("limit reached", results[1].Result?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("call_3", results[2].CallId);
        Assert.Contains("not available", results[2].Result?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] EncodeOggOpus(int channels)
    {
        const int sampleRate = 48_000;
        var frames = sampleRate / 10; // 100ms
        var samples = new short[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var value = (short)(Math.Sin(2.0 * Math.PI * 440.0 * f / sampleRate) * 12_000);
            for (var c = 0; c < channels; c++)
            {
                samples[(f * channels) + c] = value;
            }
        }

        using var ms = new MemoryStream();
        using var encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        var oggOut = new OpusOggWriteStream(encoder, ms);
        oggOut.WriteSamples(samples, 0, samples.Length);
        oggOut.Finish();
        return ms.ToArray();
    }

    private sealed class RecordingRealtimeSession : IRealtimeClientSession
    {
        public ConcurrentQueue<RealtimeClientMessage> SentMessages { get; } = new();
        public RealtimeSessionOptions? Options => null;

        public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Enqueue(message);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
