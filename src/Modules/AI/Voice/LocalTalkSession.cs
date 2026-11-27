using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Concentus;
using Concentus.Oggfile;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;
using NAudio.Wave;
using NLayer;

namespace IntoChat.OS.Modules.AI.Voice;

internal sealed class LocalTalkSession : IRealtimeClientSession
{
    internal const int InputSampleRate = 16_000;
    internal const int OutputSampleRate = 24_000;

    private readonly ISpeechToTextClient _hear;
    private readonly IChatClient _chat;
    private readonly ITextToSpeechClient _speak;
    private readonly IVoiceActivity _vad;
    private readonly LocalTalkOptions _options;
    private readonly LocalTalkToolRouter? _toolRouter;
    private readonly Func<TalkTool, CancellationToken, Task>? _toolHandler;

    private readonly Lock _vadLock = new();
    private readonly object _outputLock = new();
    private readonly Channel<ReadOnlyMemory<byte>> _utterances = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<RealtimeServerMessage> _output = Channel.CreateUnbounded<RealtimeServerMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly List<ChatMessage> _history = [];
    private readonly Task _workerTask;

    private RealtimeSessionOptions _sessionOptions;
    private string? _voice;
    private string? _instructions;

    private long _nextTurnId;
    private long _activeTurnId;
    private bool _activeTurnCancelled;
    private bool _bargeInInProgress;
    private CancellationTokenSource? _activeTurnCts;
    private bool _disposed;

    internal LocalTalkSession(
        ISpeechToTextClient hear,
        IChatClient chat,
        ITextToSpeechClient speak,
        IVoiceActivity vad,
        LocalTalkOptions options,
        RealtimeSessionOptions? sessionOptions,
        LocalTalkToolRouter? toolRouter = null)
    {
        _hear = hear;
        _chat = chat;
        _speak = speak;
        _vad = vad;
        _options = options;
        _toolRouter = toolRouter;

        _voice = sessionOptions?.Voice ?? options.Voice;
        _instructions = sessionOptions?.Instructions ?? options.Instructions;
        _sessionOptions = CloneOptions(sessionOptions, _voice, _instructions);

        if (_toolRouter is not null)
        {
            _toolHandler = EmitToolEventAsync;
            _toolRouter.Attach(_toolHandler);
        }

        _workerTask = Task.Run(ProcessUtterancesAsync);
    }

    public RealtimeSessionOptions? Options => Volatile.Read(ref _sessionOptions);

    public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _disposed))
        {
            return Task.CompletedTask;
        }

        switch (message)
        {
            case SessionUpdateRealtimeClientMessage update:
                ApplySessionUpdate(update.Options);
                return Task.CompletedTask;

            case InputAudioBufferAppendRealtimeClientMessage append:
                HandleAudioChunk(append.Content.Data.Span);
                return Task.CompletedTask;

#pragma warning disable OPENAI002 // OpenAI.Realtime cancel command forwarded by TalkEndpoint on tap-to-interrupt.
            case RealtimeClientMessage { RawRepresentation: OpenAI.Realtime.RealtimeClientCommandResponseCancel }:
                TryTriggerBargeIn();
                return Task.CompletedTask;
#pragma warning restore OPENAI002

            default:
                return Task.CompletedTask;
        }
    }

    public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        if (_toolRouter is not null && _toolHandler is not null)
        {
            _toolRouter.Detach(_toolHandler);
        }

        _utterances.Writer.TryComplete();
        await _sessionCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_outputLock)
        {
            while (_bargeInInProgress)
            {
                Monitor.Wait(_outputLock);
            }

            _output.Writer.TryComplete();
        }

        lock (_vadLock)
        {
            _vad.Dispose();
        }

        _sessionCts.Dispose();
    }

    private void ApplySessionUpdate(RealtimeSessionOptions? next)
    {
        if (next is null)
        {
            return;
        }

        if (next.Voice is not null)
        {
            Volatile.Write(ref _voice, next.Voice);
        }

        if (next.Instructions is not null)
        {
            Volatile.Write(ref _instructions, next.Instructions);
        }

        Volatile.Write(ref _sessionOptions, CloneOptions(next, Volatile.Read(ref _voice), Volatile.Read(ref _instructions)));
    }

    private void HandleAudioChunk(ReadOnlySpan<byte> pcm16)
    {
        VoiceActivity activity;
        lock (_vadLock)
        {
            if (_disposed)
            {
                return;
            }

            activity = _vad.Push(pcm16);
        }

        if (activity.Speaking && activity.SpeakingFor >= _options.BargeIn)
        {
            TryTriggerBargeIn();
        }

        if (activity.Ended is { Length: > 0 })
        {
            foreach (var segment in activity.Ended)
            {
                if (!segment.IsEmpty)
                {
                    _utterances.Writer.TryWrite(segment);
                }
            }
        }
    }

    private void TryTriggerBargeIn()
    {
        CancellationTokenSource? ctsToCancel;
        lock (_outputLock)
        {
            if (_activeTurnId == 0 || _activeTurnCancelled)
            {
                return;
            }

            _activeTurnCancelled = true;
            _bargeInInProgress = true;
            ctsToCancel = _activeTurnCts;
        }

        try
        {
            ctsToCancel?.Cancel();
        }
        catch (ObjectDisposedException error)
        {
            OsConsoleLog.For<LocalTalkSession>().LogSwallowed(error, "cancelling already-disposed LocalTalk turn token");
        }
        finally
        {
            lock (_outputLock)
            {
                _output.Writer.TryWrite(new RealtimeServerMessage { Type = LocalTalk.Interrupted });
                _bargeInInProgress = false;
                Monitor.PulseAll(_outputLock);
            }
        }
    }

    private async Task ProcessUtterancesAsync()
    {
        try
        {
            await foreach (var segment in _utterances.Reader.ReadAllAsync(_sessionCts.Token).ConfigureAwait(false))
            {
                await RunTurnAsync(segment).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunTurnAsync(ReadOnlyMemory<byte> segment)
    {
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        var turnId = Interlocked.Increment(ref _nextTurnId);

        lock (_outputLock)
        {
            while (_bargeInInProgress)
            {
                Monitor.Wait(_outputLock);
            }

            _activeTurnId = turnId;
            _activeTurnCancelled = false;
            _activeTurnCts = turnCts;
        }

        try
        {
            string? heard;
            try
            {
                using var wavStream = new MemoryStream(Pcm16.Wav(segment.Span, InputSampleRate), writable: false);
                var speech = await _hear.GetTextAsync(wavStream, options: null, turnCts.Token).ConfigureAwait(false);
                heard = speech.Text?.Trim();
            }
            catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                OsConsoleLog.For<LocalTalkSession>().LogSwallowed(error, "transcribing audio segment in LocalTalk");
                WriteTurnMessage(turnId, new ErrorRealtimeServerMessage
                {
                    Type = RealtimeServerMessageType.Error,
                    Error = new ErrorContent(error.Message),
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(heard))
            {
                return;
            }

            if (!WriteTurnMessage(turnId, new InputAudioTranscriptionRealtimeServerMessage(
                RealtimeServerMessageType.InputAudioTranscriptionCompleted)
            {
                Transcription = heard,
            }))
            {
                return;
            }

            var responseId = $"local_{turnId}";
            if (!WriteTurnMessage(turnId, new ResponseCreatedRealtimeServerMessage(
                RealtimeServerMessageType.ResponseCreated)
            {
                ResponseId = responseId,
            }))
            {
                return;
            }

            var messages = BuildConversationMessages(heard);
            var pending = new StringBuilder();
            var fullReply = new StringBuilder();
            var sentences = Channel.CreateBounded<string>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
            });
            var ttsTask = Task.Run(async () =>
            {
                await foreach (var sentence in sentences.Reader.ReadAllAsync(turnCts.Token).ConfigureAwait(false))
                {
                    if (!await SpeakSentenceAsync(turnId, responseId, sentence, turnCts.Token).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
                return true;
            }, turnCts.Token);

            try
            {
                await foreach (var update in _chat.GetStreamingResponseAsync(messages, options: null, turnCts.Token).ConfigureAwait(false))
                {
                    turnCts.Token.ThrowIfCancellationRequested();
                    var delta = update.Text;
                    if (string.IsNullOrEmpty(delta))
                    {
                        continue;
                    }

                    fullReply.Append(delta);
                    pending.Append(delta);

                    if (!WriteTurnMessage(turnId, new OutputTextAudioRealtimeServerMessage(
                        RealtimeServerMessageType.OutputTextDelta)
                    {
                        ResponseId = responseId,
                        Text = delta,
                    }))
                    {
                        sentences.Writer.TryComplete();
                        return;
                    }

                    foreach (var sentence in SentenceChunker.Complete(pending))
                    {
                        await sentences.Writer.WriteAsync(sentence, turnCts.Token).ConfigureAwait(false);
                    }
                }

                var remainder = pending.ToString().Trim();
                if (remainder.Length > 0)
                {
                    await sentences.Writer.WriteAsync(remainder, turnCts.Token).ConfigureAwait(false);
                }

                sentences.Writer.TryComplete();
                if (!await ttsTask.ConfigureAwait(false))
                {
                    return;
                }

                _history.Add(new ChatMessage(ChatRole.User, heard));
                if (fullReply.Length > 0)
                {
                    _history.Add(new ChatMessage(ChatRole.Assistant, fullReply.ToString()));
                }

                WriteTurnMessage(turnId, new ResponseCreatedRealtimeServerMessage(
                    RealtimeServerMessageType.ResponseDone)
                {
                    ResponseId = responseId,
                    Status = RealtimeResponseStatus.Completed,
                });
            }
            catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
            {
                sentences.Writer.TryComplete();
                return;
            }
            catch (Exception error)
            {
                sentences.Writer.TryComplete();
                OsConsoleLog.For<LocalTalkSession>().LogSwallowed(error, "generating or speaking reply in LocalTalk");
                WriteTurnMessage(turnId, new ErrorRealtimeServerMessage
                {
                    Type = RealtimeServerMessageType.Error,
                    Error = new ErrorContent(error.Message),
                });
            }
        }
        finally
        {
            lock (_outputLock)
            {
                if (_activeTurnId == turnId)
                {
                    _activeTurnId = 0;
                    _activeTurnCts = null;
                }
            }
        }
    }

    private async Task<bool> SpeakSentenceAsync(long turnId, string responseId, string sentence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var voice = Volatile.Read(ref _voice);
        var options = string.IsNullOrWhiteSpace(voice) ? null : new TextToSpeechOptions { VoiceId = voice };
        var response = await _speak.GetAudioAsync(sentence, options, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var data = response.Contents.OfType<DataContent>().FirstOrDefault(static c => !c.Data.IsEmpty);
        if (data is null)
        {
            return true;
        }

        var pcm = NormalizeToOutputPcm(data);
        return WriteTurnMessage(turnId, new OutputTextAudioRealtimeServerMessage(
            RealtimeServerMessageType.OutputAudioDelta)
        {
            ResponseId = responseId,
            Audio = Convert.ToBase64String(pcm),
        });
    }

    internal static byte[] NormalizeToOutputPcm(DataContent data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var bytes = data.Data.ToArray();
        if (bytes.Length == 0)
        {
            return [];
        }

        var mediaType = NormalizeMediaType(data.MediaType);

        if (mediaType.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase))
        {
            var rate = SpeechEndpoint.ParsePcmRate(data.MediaType!);
            return rate == OutputSampleRate
                ? bytes
                : Pcm16.Resample(bytes, rate, OutputSampleRate);
        }

        if (mediaType is "audio/wav" or "audio/x-wav" or "audio/wave" || LooksLikeWav(bytes))
        {
            return DecodeWavToOutputPcm(bytes);
        }

        if (mediaType is "audio/mpeg" or "audio/mp3" || LooksLikeMp3(bytes))
        {
            return DecodeMp3ToOutputPcm(bytes);
        }

        if (mediaType is "audio/ogg" or "audio/opus" || LooksLikeOgg(bytes))
        {
            return DecodeOpusToOutputPcm(bytes);
        }

        throw new NotSupportedException($"Unsupported TTS audio media type '{data.MediaType ?? "unknown"}' for LocalTalk output.");
    }

    private Task EmitToolEventAsync(TalkTool tool, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || Volatile.Read(ref _disposed))
        {
            return Task.CompletedTask;
        }

        lock (_outputLock)
        {
            if (_activeTurnId == 0 || _activeTurnCancelled)
            {
                return Task.CompletedTask;
            }

            _output.Writer.TryWrite(new RealtimeServerMessage
            {
                Type = LocalTalk.ToolEvent,
                RawRepresentation = tool,
            });
        }

        return Task.CompletedTask;
    }

    private static string NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return string.Empty;
        }

        var semi = mediaType.IndexOf(';', StringComparison.Ordinal);
        return (semi >= 0 ? mediaType[..semi] : mediaType).Trim().ToLowerInvariant();
    }

    private static bool LooksLikeWav(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';

    private static bool LooksLikeOgg(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 4
            && bytes[0] == (byte)'O' && bytes[1] == (byte)'g' && bytes[2] == (byte)'g' && bytes[3] == (byte)'S';

    private static bool LooksLikeMp3(ReadOnlySpan<byte> bytes)
        => (bytes.Length >= 3 && bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3')
            || (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0);

    private static byte[] DecodeWavToOutputPcm(byte[] wavBytes)
    {
        using var ms = new MemoryStream(wavBytes, writable: false);
        using var reader = new WaveFileReader(ms);
        var sampleProvider = reader.ToSampleProvider();
        var channels = Math.Max(1, reader.WaveFormat.Channels);
        var buffer = new float[4096 * channels];
        using var outMs = new MemoryStream();
        int read;
        while ((read = sampleProvider.Read(buffer.AsSpan())) > 0)
        {
            WriteFloatToMonoPcm16(outMs, buffer.AsSpan(0, read), channels);
        }

        var pcm = outMs.ToArray();
        return reader.WaveFormat.SampleRate == OutputSampleRate
            ? pcm
            : Pcm16.Resample(pcm, reader.WaveFormat.SampleRate, OutputSampleRate);
    }

    private static byte[] DecodeMp3ToOutputPcm(byte[] mp3Bytes)
    {
        using var ms = new MemoryStream(mp3Bytes, writable: false);
        using var mpeg = new MpegFile(ms);
        var channels = Math.Max(1, mpeg.Channels);
        var sampleRate = mpeg.SampleRate > 0 ? mpeg.SampleRate : OutputSampleRate;
        var floatBuf = new float[4096 * channels];
        using var outMs = new MemoryStream();
        int read;
        while ((read = mpeg.ReadSamples(floatBuf, 0, floatBuf.Length)) > 0)
        {
            WriteFloatToMonoPcm16(outMs, floatBuf.AsSpan(0, read), channels);
        }

        var pcm = outMs.ToArray();
        return sampleRate == OutputSampleRate
            ? pcm
            : Pcm16.Resample(pcm, sampleRate, OutputSampleRate);
    }

    private static byte[] DecodeOpusToOutputPcm(byte[] opusBytes)
    {
        if (LooksLikeOgg(opusBytes))
        {
            const int oggSampleRate = 48_000;
            var channels = DetectOggOpusChannels(opusBytes);
            using var ms = new MemoryStream(opusBytes, writable: false);
            using var decoder = OpusCodecFactory.CreateDecoder(oggSampleRate, channels);
            var oggIn = new OpusOggReadStream(decoder, ms);
            using var outMs = new MemoryStream();
            Span<byte> pair = stackalloc byte[2];
            while (oggIn.HasNextPacket)
            {
                var samples = oggIn.DecodeNextPacket();
                if (samples is not { Length: > 0 })
                {
                    continue;
                }

                if (channels == 1)
                {
                    var bytes = new byte[samples.Length * sizeof(short)];
                    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                    outMs.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    var frames = samples.Length / channels;
                    for (var f = 0; f < frames; f++)
                    {
                        int sum = 0;
                        var offset = f * channels;
                        for (var c = 0; c < channels; c++)
                        {
                            sum += samples[offset + c];
                        }

                        var mono = (short)Math.Clamp(sum / channels, short.MinValue, short.MaxValue);
                        BinaryPrimitives.WriteInt16LittleEndian(pair, mono);
                        outMs.Write(pair);
                    }
                }
            }

            var pcm48k = outMs.ToArray();
            return pcm48k.Length == 0
                ? []
                : Pcm16.Resample(pcm48k, oggSampleRate, OutputSampleRate);
        }

        // Raw single Opus packet (RFC 6716) decoded directly at 24 kHz mono.
        using var rawDecoder = OpusCodecFactory.CreateDecoder(OutputSampleRate, 1);
        var frameSamples = new short[OutputSampleRate * 120 / 1000]; // up to 120 ms per Opus packet
        var decodedSamples = rawDecoder.Decode(opusBytes.AsSpan(), frameSamples.AsSpan(), frameSamples.Length, false);
        if (decodedSamples <= 0)
        {
            return [];
        }

        var rawPcm = new byte[decodedSamples * sizeof(short)];
        for (var i = 0; i < decodedSamples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(rawPcm.AsSpan(i * 2, 2), frameSamples[i]);
        }

        return rawPcm;
    }

    private static int DetectOggOpusChannels(ReadOnlySpan<byte> oggBytes)
    {
        // Search the first Ogg page for the RFC 7845 "OpusHead" identification header; byte +9 is channel count.
        ReadOnlySpan<byte> magic = "OpusHead"u8;
        var limit = Math.Min(oggBytes.Length, 512);
        var idx = oggBytes[..limit].IndexOf(magic);
        if (idx >= 0 && idx + 9 < oggBytes.Length)
        {
            var ch = oggBytes[idx + 9];
            if (ch is 1 or 2)
            {
                return ch;
            }
        }

        return 1;
    }

    private static void WriteFloatToMonoPcm16(MemoryStream output, ReadOnlySpan<float> samples, int channels)
    {
        var frames = samples.Length / channels;
        Span<byte> pair = stackalloc byte[2];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0f;
            var offset = f * channels;
            for (var c = 0; c < channels; c++)
            {
                sum += samples[offset + c];
            }

            var mono = Math.Clamp(sum / channels, -1.0f, 1.0f);
            var sample = (short)Math.Round(mono * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pair, sample);
            output.Write(pair);
        }
    }

    private List<ChatMessage> BuildConversationMessages(string heard)
    {
        var messages = new List<ChatMessage>(_history.Count + 2);
        var instructions = Volatile.Read(ref _instructions);
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            messages.Add(new ChatMessage(ChatRole.System, instructions));
        }

        messages.AddRange(_history);
        messages.Add(new ChatMessage(ChatRole.User, heard));
        return messages;
    }

    private bool WriteTurnMessage(long turnId, RealtimeServerMessage message)
    {
        lock (_outputLock)
        {
            if (turnId != _activeTurnId || _activeTurnCancelled)
            {
                return false;
            }

            _output.Writer.TryWrite(message);
            return true;
        }
    }

    private static RealtimeSessionOptions CloneOptions(RealtimeSessionOptions? source, string? voice, string? instructions)
        => new()
        {
            Model = source?.Model,
            Voice = voice,
            Instructions = instructions,
            InputAudioFormat = source?.InputAudioFormat ?? new RealtimeAudioFormat("audio/pcm", InputSampleRate),
            OutputAudioFormat = source?.OutputAudioFormat ?? new RealtimeAudioFormat("audio/pcm", OutputSampleRate),
            MaxOutputTokens = source?.MaxOutputTokens,
            OutputModalities = source?.OutputModalities,
            ToolMode = source?.ToolMode,
            Tools = source?.Tools,
            TranscriptionOptions = source?.TranscriptionOptions,
            VoiceActivityDetection = source?.VoiceActivityDetection,
        };
}
