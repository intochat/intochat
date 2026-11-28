using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DigitalBrain.Log;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Voice;

// Full-duplex realtime audio/event bridge between a TalkWebSocketTransport and an IRealtimeClientSession.
internal sealed class TalkSessionBridge(
    TalkWebSocketTransport transport,
    IRealtimeClientSession session,
    IConversation conversation,
    IIntoChatOSBrains brains,
    string brainId,
    TalkVoice talk,
    TimeProvider clock,
    ILogger logger)
{
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(2);

    private readonly TalkToolDispatcher _tools = TalkToolDispatcher.ForSession(transport, session, conversation, brains, brainId, logger);
    private readonly TurnAccumulator _turnState = new();

    public async Task RunAsync(CancellationToken sessionLimitCt)
    {
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(sessionLimitCt);
        var lastAudioTimestamp = clock.GetTimestamp();

        var inbound = PumpClientToSessionAsync(
            () => Volatile.Write(ref lastAudioTimestamp, clock.GetTimestamp()),
            () => Volatile.Read(ref lastAudioTimestamp),
            pumpCts);

        var outbound = PumpSessionToClientAsync(pumpCts);

        try
        {
            await Task.WhenAll(inbound, outbound).ConfigureAwait(false);
        }
        finally
        {
            await _tools.CancelAndDrainAsync().ConfigureAwait(false);
        }

        await transport.CloseSafelyAsync(WebSocketCloseStatus.NormalClosure, "Session ended.").ConfigureAwait(false);
    }

    private async Task PumpClientToSessionAsync(Action markAudio, Func<long> readLastAudio, CancellationTokenSource pumpCts)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!pumpCts.IsCancellationRequested && transport.IsOpen)
            {
                var remainingSilence = SilenceTimeout - clock.GetElapsedTime(readLastAudio());
                if (remainingSilence <= TimeSpan.Zero)
                {
                    await pumpCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                using var silenceCts = CancellationTokenSource.CreateLinkedTokenSource(pumpCts.Token);
                silenceCts.CancelAfter(remainingSilence);

                var (messageType, payload) = await transport.ReceiveFrameAsync(buffer, silenceCts.Token).ConfigureAwait(false);
                if (messageType == WebSocketMessageType.Close)
                {
                    await pumpCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (messageType == WebSocketMessageType.Binary)
                {
                    if (payload.Length == 0) { continue; }
                    markAudio();
                    var pcm = talk.InputRate == TalkEndpoint.ClientInputRate
                        ? payload
                        : Pcm16.Resample(payload, TalkEndpoint.ClientInputRate, talk.InputRate);
                    await session.SendAsync(new InputAudioBufferAppendRealtimeClientMessage(new DataContent(pcm, "audio/pcm")), pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (messageType == WebSocketMessageType.Text && payload.Length > 0)
                {
                    TalkEvent? ev;
                    try { ev = JsonSerializer.Deserialize(payload, TalkJson.Default.TalkEvent); }
                    catch (JsonException) { continue; }

                    switch (ev)
                    {
                        case TalkEnd:
                            await pumpCts.CancelAsync().ConfigureAwait(false);
                            return;

                        case TalkInterrupt:
                            _tools.CancelActive();
                            _turnState.Reset();
#pragma warning disable OPENAI002 // OpenAI.Realtime cancel command forwarded to session.
                            await session.SendAsync(
                                new RealtimeClientMessage { RawRepresentation = new OpenAI.Realtime.RealtimeClientCommandResponseCancel() },
                                pumpCts.Token).ConfigureAwait(false);
#pragma warning restore OPENAI002
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { await pumpCts.CancelAsync().ConfigureAwait(false); }
        catch (WebSocketException error)
        {
            logger.LogSwallowed(error, "reading client WebSocket frame in TalkSessionBridge");
            await pumpCts.CancelAsync().ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            logger.LogSwallowed(error, "rejecting oversized client frame in TalkSessionBridge");
            await pumpCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task PumpSessionToClientAsync(CancellationTokenSource pumpCts)
    {
        var turnCounter = 0;
        var pendingToolCalls = new List<FunctionCallContent>();
        try
        {
            await foreach (var message in session.GetStreamingResponseAsync(pumpCts.Token).ConfigureAwait(false))
            {
                if (IsBargeInSignal(message))
                {
                    _tools.CancelActive();
                    _turnState.Reset();
                    pendingToolCalls.Clear();
                    await transport.SendEventAsync(new TalkCancel(), pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message.Type == LocalTalk.ToolEvent && message.RawRepresentation is TalkTool toolEvent)
                {
                    await transport.SendEventAsync(toolEvent, pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message is ErrorRealtimeServerMessage errorMessage)
                {
                    var text = errorMessage.Error?.Message;
                    await transport.SendEventAsync(new TalkError(string.IsNullOrWhiteSpace(text) ? "Voice session error." : text), pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message is InputAudioTranscriptionRealtimeServerMessage transcription
                    && message.Type == RealtimeServerMessageType.InputAudioTranscriptionCompleted
                    && !string.IsNullOrWhiteSpace(transcription.Transcription))
                {
                    var heard = transcription.Transcription.Trim();
                    _turnState.SetHeard(heard);
                    await transport.SendEventAsync(new TalkHeard(heard), pumpCts.Token).ConfigureAwait(false);
                    await transport.SendEventAsync(new TalkState("thinking"), pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message is ResponseOutputItemRealtimeServerMessage outputItem
                    && message.Type == RealtimeServerMessageType.ResponseOutputItemDone
                    && outputItem.Item?.Contents is { Count: > 0 } itemContents)
                {
                    pendingToolCalls.AddRange(itemContents.OfType<FunctionCallContent>());
                    continue;
                }

                if (message is OutputTextAudioRealtimeServerMessage output)
                {
                    await HandleOutputDeltaAsync(message.Type, output, pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message.Type == RealtimeServerMessageType.ResponseDone)
                {
                    await HandleResponseDoneAsync(message as ResponseCreatedRealtimeServerMessage, pendingToolCalls, ++turnCounter, pumpCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            logger.LogSwallowed(error, "pumping realtime session messages to WebSocket client");
            await transport.SendEventAsync(new TalkError(error.Message), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleOutputDeltaAsync(RealtimeServerMessageType type, OutputTextAudioRealtimeServerMessage output, CancellationToken ct)
    {
        if (type == RealtimeServerMessageType.OutputAudioDelta && !string.IsNullOrWhiteSpace(output.Audio))
        {
            byte[] pcm;
            try { pcm = Convert.FromBase64String(output.Audio); }
            catch (FormatException error)
            {
                logger.LogSwallowed(error, "decoding output audio delta in TalkSessionBridge");
                return;
            }

            if (pcm.Length > 0)
            {
                if (_turnState.MarkSpeaking()) { await transport.SendEventAsync(new TalkState("speaking"), ct).ConfigureAwait(false); }
                await transport.SendBinaryAsync(pcm, ct).ConfigureAwait(false);
            }

            return;
        }

        if (type == RealtimeServerMessageType.OutputTextDelta && !string.IsNullOrEmpty(output.Text))
        {
            _turnState.AppendText(output.Text);
            await transport.SendEventAsync(new TalkReply(output.Text), ct).ConfigureAwait(false);
        }
        else if (type == RealtimeServerMessageType.OutputAudioTranscriptionDelta && !string.IsNullOrEmpty(output.Text) && _turnState.AppendTranscript(output.Text))
        {
            await transport.SendEventAsync(new TalkReply(output.Text), ct).ConfigureAwait(false);
        }
    }

    private async Task HandleResponseDoneAsync(
        ResponseCreatedRealtimeServerMessage? done,
        List<FunctionCallContent> pendingToolCalls,
        int turnCounter,
        CancellationToken ct)
    {
        var allCalls = new List<FunctionCallContent>(pendingToolCalls);
        pendingToolCalls.Clear();
        if (done?.Items is { Count: > 0 })
        {
            allCalls.AddRange(done.Items.Where(static i => i.Contents is { Count: > 0 }).SelectMany(static i => i.Contents.OfType<FunctionCallContent>()));
        }

        if (allCalls.Count > 0)
        {
            _tools.DispatchInBackground(allCalls.ToArray(), _turnState.PeekHeard, _turnState.MarkDelegatedToBrain, ct);
            return;
        }

        var (heard, reply, delegated) = _turnState.Complete();
        if (!talk.RecordsItself
            && !delegated
            && done?.Status != RealtimeResponseStatus.Cancelled
            && !string.IsNullOrWhiteSpace(heard)
            && !string.IsNullOrWhiteSpace(reply)
            && talk.Model is { Value.Length: > 0 } model)
        {
            var responseId = !string.IsNullOrWhiteSpace(done?.ResponseId) ? done.ResponseId : $"voice-{turnCounter}";
            try
            {
                await conversation.Voiced(heard, reply, model, responseId).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogSwallowed(error, "recording cloud voice exchange into conversation");
                await transport.SendEventAsync(new TalkError("Could not save the spoken exchange."), ct).ConfigureAwait(false);
            }
        }

        await transport.SendEventAsync(new TalkState("listening"), ct).ConfigureAwait(false);
    }

    private static bool IsBargeInSignal(RealtimeServerMessage message)
    {
        if (message.Type == LocalTalk.Interrupted) { return true; }
#pragma warning disable OPENAI002 // OpenAI.Realtime server VAD speech-started update type.
        return message.RawRepresentation is OpenAI.Realtime.RealtimeServerUpdateInputAudioBufferSpeechStarted;
#pragma warning restore OPENAI002
    }

    private sealed class TurnAccumulator
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _textReply = new();
        private readonly StringBuilder _transcriptReply = new();
        private string? _heard;
        private bool _speaking;
        private bool _delegatedToBrain;

        public void SetHeard(string heard) { lock (_gate) { ResetUnlocked(); _heard = heard; } }
        public string? PeekHeard() { lock (_gate) { return _heard; } }
        public void MarkDelegatedToBrain() { lock (_gate) { _delegatedToBrain = true; } }
        public void AppendText(string delta) { lock (_gate) { _textReply.Append(delta); } }
        public void Reset() { lock (_gate) { ResetUnlocked(); } }

        public bool MarkSpeaking()
        {
            lock (_gate)
            {
                if (_speaking) { return false; }
                _speaking = true;
                return true;
            }
        }

        public bool AppendTranscript(string delta)
        {
            lock (_gate)
            {
                _transcriptReply.Append(delta);
                return _textReply.Length == 0;
            }
        }

        public (string? Heard, string Reply, bool DelegatedToBrain) Complete()
        {
            lock (_gate)
            {
                var result = (_heard, (_textReply.Length > 0 ? _textReply : _transcriptReply).ToString().Trim(), _delegatedToBrain);
                ResetUnlocked();
                return result;
            }
        }

        private void ResetUnlocked()
        {
            _heard = null;
            _textReply.Clear();
            _transcriptReply.Clear();
            _speaking = false;
            _delegatedToBrain = false;
        }
    }
}
