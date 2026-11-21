using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Devices.Voice;
using Microsoft.Extensions.AI;

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
        RealtimeSessionOptions? sessionOptions)
    {
        _hear = hear;
        _chat = chat;
        _speak = speak;
        _vad = vad;
        _options = options;

        _voice = sessionOptions?.Voice ?? options.Voice;
        _instructions = sessionOptions?.Instructions ?? options.Instructions;
        _sessionOptions = CloneOptions(sessionOptions, _voice, _instructions);

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
                        return;
                    }

                    foreach (var sentence in SentenceChunker.Complete(pending))
                    {
                        if (!await SpeakSentenceAsync(turnId, responseId, sentence, turnCts.Token).ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                }

                var remainder = pending.ToString().Trim();
                if (remainder.Length > 0)
                {
                    if (!await SpeakSentenceAsync(turnId, responseId, remainder, turnCts.Token).ConfigureAwait(false))
                    {
                        return;
                    }
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
                return;
            }
            catch (Exception error)
            {
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

    private static byte[] NormalizeToOutputPcm(DataContent data)
    {
        if (data.MediaType is { Length: > 0 } mt && mt.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase))
        {
            var rate = SpeechEndpoint.ParsePcmRate(mt);
            return rate == OutputSampleRate
                ? data.Data.ToArray()
                : Pcm16.Resample(data.Data.Span, rate, OutputSampleRate);
        }

        return data.Data.ToArray();
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
