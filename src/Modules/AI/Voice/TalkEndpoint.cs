using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI.Voice;

// Full-duplex WebSocket endpoint for voice mode (GET /ai/talk?conversation={id}).
// Wire: client -> Kernel PCM16 LE mono 16 kHz; Kernel -> client PCM16 LE mono 24 kHz; text frames are TalkJson TalkEvent.
internal static class TalkEndpoint
{
    internal const int ClientInputRate = 16_000;
    internal const int ClientOutputRate = 24_000;
    private const int MaxBinaryFrameBytes = 1024 * 1024;
    private const int MaxTextFrameBytes = 64 * 1024;
    private const int MaxCloseReasonBytes = 123;
    private const int HistorySaidCount = 20;

    private static readonly TimeSpan MaxSessionDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(2);

    internal const string BrainTurnToolName = "run_brain_turn";
    internal const int MaxBrainTurnCallsPerSession = 24;
    private const int MaxHandledToolCallsPerSession = 64;

    internal const string DefaultPersona =
        "You are a helpful voice assistant. Keep spoken answers clear, natural, and concise. "
        + "When the user asks you to run OS tools, inspect memory, search the web, check files, or open/build apps, "
        + "briefly acknowledge in 3 to 5 natural words (for example, \"On it, checking now...\") while calling the 'run_brain_turn' tool "
        + "with their request, and then speak a concise summary of the result.";

    private static readonly string[] ForbiddenQueryAuthParameters =
        ["access_token", "token", "authorization", "bearer"];

    public static async Task<IResult> MintTicketAsync(
        HttpContext http,
        TalkTicketStore tickets,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tickets);

        http.Response.Headers.CacheControl = "no-store";

        if (AIModule.StampedCaller(http) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (!AIModule.IsDirectPersonCaller(caller))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        string? rawConversation = null;
        if (http.Request.ContentLength is > 0 || http.Request.HasJsonContentType())
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync(
                    http.Request.Body,
                    TalkJson.Default.TalkTicketRequest,
                    ct).ConfigureAwait(false);
                rawConversation = body?.Conversation;
            }
            catch (JsonException)
            {
                return Results.Problem("Supply a JSON body with a 'conversation' id.", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        if (string.IsNullOrWhiteSpace(rawConversation))
        {
            rawConversation = http.Request.Query["conversation"].ToString();
        }

        if (string.IsNullOrWhiteSpace(rawConversation) || !ConversationId.TryParse(rawConversation, out var conversationId))
        {
            return Results.Problem("A valid conversation id is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var (ticket, expiresInSeconds) = tickets.Issue(caller, conversationId);
            return Results.Json(new TalkTicketResponse(ticket, expiresInSeconds), TalkJson.Default.TalkTicketResponse);
        }
        catch (InvalidOperationException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status429TooManyRequests);
        }
    }

    public static async Task Run(
        HttpContext http,
        INeuronProxies brain,
        IVoiceClients voice,
        IIntoChatOSBrains brains,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(brains);

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Reject query-string bearer tokens explicitly (never accept secrets in URLs).
        if (HasForbiddenQueryAuthToken(http.Request.Query))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var cors = http.RequestServices.GetService<IOptions<KernelCorsOptions>>()?.Value;
        if (!IsOriginAllowed(http.Request, cors))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var rawConversation = http.Request.Query["conversation"].ToString();
        if (string.IsNullOrWhiteSpace(rawConversation) || !ConversationId.TryParse(rawConversation, out var conversationId))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var stampedCaller = AIModule.StampedCaller(http);
        var rawTicket = http.Request.Query["ticket"].ToString();
        Caller caller;
        IDisposable? ticketCallerScope = null;

        if (!string.IsNullOrWhiteSpace(rawTicket))
        {
            var ticketStore = http.RequestServices.GetService<TalkTicketStore>();
            if (ticketStore is null || !ticketStore.TryRedeem(rawTicket, conversationId, out var redeemedCaller))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (stampedCaller is not null && !SamePrincipal(stampedCaller, redeemedCaller))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            caller = redeemedCaller;
            if (stampedCaller is null)
            {
                ticketCallerScope = http.RequestServices.GetRequiredService<EdgeAuthority>().Enter(caller);
            }
        }
        else if (stampedCaller is not null)
        {
            caller = stampedCaller;
        }
        else
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var _ = ticketCallerScope;

        if (!AIModule.IsDirectPersonCaller(caller))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("IntoChat.OS.Modules.AI.Voice.TalkEndpoint")
            ?? OsConsoleLog.For<TalkEvent>();

        var talk = await voice.Talk(caller.Brain.Value, conversationId, ct).ConfigureAwait(false);
        if (talk.Client is null)
        {
            var reason = talk.Unavailable ?? "Voice mode is unavailable.";
            using var errorSocket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var errorSendLock = new SemaphoreSlim(1, 1);
            await SendEventAsync(errorSocket, errorSendLock, new TalkError(reason), ct).ConfigureAwait(false);
            await CloseSafelyAsync(errorSocket, WebSocketCloseStatus.InternalServerError, reason).ConfigureAwait(false);
            return;
        }

        using var realtimeClient = talk.Client;
        var conversation = brain.Get<IConversation>(new NeuronId(caller.Brain, IConversation.Type, IConversation.KeyOf(conversationId)));

        string? instructions = null;
        IReadOnlyList<AITool>? tools = null;
        ChatToolMode? toolMode = null;
        if (!talk.RecordsItself)
        {
            instructions = await BuildCloudInstructionsAsync(conversation, logger, ct).ConfigureAwait(false);
            tools = [CreateBrainTurnTool()];
            toolMode = ChatToolMode.Auto;
        }

        var voicePersona = await VoiceSettings.ReadVoicePersonaAsync(brain, caller.Brain, ct).ConfigureAwait(false);

        var sessionOptions = new RealtimeSessionOptions
        {
            Model = talk.Model?.Value,
            Voice = voicePersona,
            InputAudioFormat = new RealtimeAudioFormat("audio/pcm", talk.InputRate),
            OutputAudioFormat = new RealtimeAudioFormat("audio/pcm", ClientOutputRate),
            VoiceActivityDetection = new VoiceActivityDetectionOptions
            {
                Enabled = true,
                AllowInterruption = true,
            },
            Instructions = instructions,
            Tools = tools,
            ToolMode = toolMode,
        };

        using var ws = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var sendLock = new SemaphoreSlim(1, 1);
        using var sessionLimitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionLimitCts.CancelAfter(MaxSessionDuration);

        IRealtimeClientSession session;
        try
        {
            session = await realtimeClient.CreateSessionAsync(sessionOptions, sessionLimitCts.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, "opening realtime voice session");
            await SendEventAsync(ws, sendLock, new TalkError(error.Message), ct).ConfigureAwait(false);
            await CloseSafelyAsync(ws, WebSocketCloseStatus.InternalServerError, error.Message).ConfigureAwait(false);
            return;
        }

        await using (session.ConfigureAwait(false))
        {
            var clock = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(sessionLimitCts.Token);
            var lastAudioTimestamp = clock.GetTimestamp();
            var turnState = new TurnAccumulator();
            var activeTools = new ActiveToolExecution();

            var inbound = PumpClientToSessionAsync(
                ws,
                session,
                talk.InputRate,
                clock,
                () => Volatile.Write(ref lastAudioTimestamp, clock.GetTimestamp()),
                () => Volatile.Read(ref lastAudioTimestamp),
                turnState,
                activeTools,
                logger,
                pumpCts);

            var outbound = PumpSessionToClientAsync(
                ws,
                sendLock,
                session,
                conversation,
                brains,
                caller.Brain.Value,
                talk,
                turnState,
                activeTools,
                logger,
                pumpCts);

            try
            {
                await Task.WhenAll(inbound, outbound).ConfigureAwait(false);
            }
            finally
            {
                await activeTools.CancelAndDrainAsync().ConfigureAwait(false);
            }

            await CloseSafelyAsync(ws, WebSocketCloseStatus.NormalClosure, "Session ended.").ConfigureAwait(false);
        }
    }

    internal static bool HasForbiddenQueryAuthToken(IQueryCollection query)
    {
        foreach (var name in ForbiddenQueryAuthParameters)
        {
            if (query.ContainsKey(name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SamePrincipal(Caller left, Caller right)
        => left.Brain == right.Brain
            && string.Equals(left.For?.Principal, right.For?.Principal, StringComparison.Ordinal)
            && string.Equals(left.For?.Account, right.For?.Account, StringComparison.Ordinal);

    internal static bool IsOriginAllowed(HttpRequest request, KernelCorsOptions? cors)
    {
        var origins = request.Headers.Origin;
        if (origins.Count == 0)
        {
            return true;
        }

        if (origins.Count > 1)
        {
            return false;
        }

        var rawOrigin = origins[0];
        if (string.IsNullOrWhiteSpace(rawOrigin))
        {
            return true;
        }

        var normalized = NormalizeOrigin(rawOrigin);
        if (normalized is null)
        {
            return false;
        }

        var ownOrigin = request.Host.HasValue
            ? NormalizeOrigin($"{request.Scheme}://{request.Host.Value}")
            : null;
        if (ownOrigin is not null && string.Equals(normalized, ownOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var configuredOrigin = !string.IsNullOrWhiteSpace(cors?.AllowedOrigin)
            ? NormalizeOrigin(cors.AllowedOrigin)
            : null;
        return configuredOrigin is not null && string.Equals(normalized, configuredOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOrigin(string raw)
    {
        var trimmed = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.Length > 1))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort
            ? $"{uri.Scheme.ToLowerInvariant()}://{host}"
            : $"{uri.Scheme.ToLowerInvariant()}://{host}:{uri.Port}";
    }

    private static async Task<string> BuildCloudInstructionsAsync(
        IConversation conversation,
        ILogger logger,
        CancellationToken ct)
    {
        var sb = new StringBuilder(DefaultPersona);
        try
        {
            var view = await conversation.Read().WaitAsync(ct).ConfigureAwait(false);
            var recent = view.Entries
                .OfType<Said>()
                .TakeLast(HistorySaidCount)
                .ToArray();
            if (recent.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("Recent conversation context:");
                foreach (var entry in recent)
                {
                    var speaker = entry.By is Participant.Human ? "User" : "Assistant";
                    sb.Append(speaker).Append(": ").AppendLine(entry.Text);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogSwallowed(error, "reading conversation history for cloud realtime instructions");
        }

        return sb.ToString();
    }

    private static async Task PumpClientToSessionAsync(
        WebSocket ws,
        IRealtimeClientSession session,
        int targetInputRate,
        TimeProvider clock,
        Action markAudio,
        Func<long> readLastAudio,
        TurnAccumulator turnState,
        ActiveToolExecution activeTools,
        ILogger logger,
        CancellationTokenSource pumpCts)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!pumpCts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var remainingSilence = SilenceTimeout - clock.GetElapsedTime(readLastAudio());
                if (remainingSilence <= TimeSpan.Zero)
                {
                    await pumpCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                using var silenceCts = CancellationTokenSource.CreateLinkedTokenSource(pumpCts.Token);
                silenceCts.CancelAfter(remainingSilence);

                var (messageType, payload) = await ReceiveFrameAsync(ws, buffer, silenceCts.Token).ConfigureAwait(false);
                if (messageType == WebSocketMessageType.Close)
                {
                    await pumpCts.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (messageType == WebSocketMessageType.Binary)
                {
                    if (payload.Length == 0)
                    {
                        continue;
                    }

                    markAudio();
                    var pcm = targetInputRate == ClientInputRate
                        ? payload
                        : Pcm16.Resample(payload, ClientInputRate, targetInputRate);

                    var append = new InputAudioBufferAppendRealtimeClientMessage(new DataContent(pcm, "audio/pcm"));
                    await session.SendAsync(append, pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (messageType == WebSocketMessageType.Text && payload.Length > 0)
                {
                    TalkEvent? ev;
                    try
                    {
                        ev = JsonSerializer.Deserialize(payload, TalkJson.Default.TalkEvent);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    switch (ev)
                    {
                        case TalkEnd:
                            await pumpCts.CancelAsync().ConfigureAwait(false);
                            return;

                        case TalkInterrupt:
                            activeTools.CancelActive();
                            turnState.Reset();
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
        catch (OperationCanceledException)
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
        }
        catch (WebSocketException error)
        {
            logger.LogSwallowed(error, "reading client WebSocket frame in TalkEndpoint");
            await pumpCts.CancelAsync().ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            logger.LogSwallowed(error, "rejecting oversized client frame in TalkEndpoint");
            await pumpCts.CancelAsync().ConfigureAwait(false);
        }
    }

    internal static AITool CreateBrainTurnTool()
        => AIFunctionFactory.Create(
            (string request) => request,
            name: BrainTurnToolName,
            description: "Delegate a request to the OS brain (DeepSeek + OS tools, memory, web search, and apps). Use this whenever the user asks to run tools, inspect memory, search the web, or open/build apps.");

    internal static async Task<string> ExecuteBrainTurnAsync(
        IConversation conversation,
        IIntoChatOSBrains brains,
        string brainId,
        string request,
        CancellationToken ct,
        Func<TalkTool, CancellationToken, Task>? onToolEvent = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(brains);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);

        var watch = brains.Open(brainId);
        using var chat = new ConversationChatClient(conversation, watch, onToolEvent);
        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, request)], options: null, ct).ConfigureAwait(false);
        var text = response.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? "Done." : text;
    }

    private static async Task PumpSessionToClientAsync(
        WebSocket ws,
        SemaphoreSlim sendLock,
        IRealtimeClientSession session,
        IConversation conversation,
        IIntoChatOSBrains brains,
        string brainId,
        TalkVoice talk,
        TurnAccumulator turnState,
        ActiveToolExecution activeTools,
        ILogger logger,
        CancellationTokenSource pumpCts)
    {
        var turnCounter = 0;
        var brainTurnCalls = new BrainTurnCounter();
        var handledToolCalls = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var pendingToolCalls = new List<FunctionCallContent>();
        try
        {
            await foreach (var message in session.GetStreamingResponseAsync(pumpCts.Token).ConfigureAwait(false))
            {
                if (IsBargeInSignal(message))
                {
                    activeTools.CancelActive();
                    turnState.Reset();
                    pendingToolCalls.Clear();
                    await SendEventAsync(ws, sendLock, new TalkCancel(), pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message.Type == LocalTalk.ToolEvent && message.RawRepresentation is TalkTool toolEvent)
                {
                    await SendEventAsync(ws, sendLock, toolEvent, pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message is ErrorRealtimeServerMessage errorMessage)
                {
                    var text = errorMessage.Error?.Message;
                    await SendEventAsync(
                        ws,
                        sendLock,
                        new TalkError(string.IsNullOrWhiteSpace(text) ? "Voice session error." : text),
                        pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message is InputAudioTranscriptionRealtimeServerMessage transcription
                    && message.Type == RealtimeServerMessageType.InputAudioTranscriptionCompleted
                    && !string.IsNullOrWhiteSpace(transcription.Transcription))
                {
                    var heard = transcription.Transcription.Trim();
                    turnState.SetHeard(heard);
                    await SendEventAsync(ws, sendLock, new TalkHeard(heard), pumpCts.Token).ConfigureAwait(false);
                    await SendEventAsync(ws, sendLock, new TalkState("thinking"), pumpCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (message is ResponseOutputItemRealtimeServerMessage outputItem
                    && message.Type == RealtimeServerMessageType.ResponseOutputItemDone
                    && outputItem.Item?.Contents is { Count: > 0 } itemContents)
                {
                    foreach (var call in itemContents.OfType<FunctionCallContent>())
                    {
                        pendingToolCalls.Add(call);
                    }
                    continue;
                }

                if (message is OutputTextAudioRealtimeServerMessage output)
                {
                    if (message.Type == RealtimeServerMessageType.OutputAudioDelta
                        && !string.IsNullOrWhiteSpace(output.Audio))
                    {
                        byte[] pcm;
                        try
                        {
                            pcm = Convert.FromBase64String(output.Audio);
                        }
                        catch (FormatException error)
                        {
                            logger.LogSwallowed(error, "decoding output audio delta in TalkEndpoint");
                            continue;
                        }

                        if (pcm.Length > 0)
                        {
                            if (turnState.MarkSpeaking())
                            {
                                await SendEventAsync(ws, sendLock, new TalkState("speaking"), pumpCts.Token).ConfigureAwait(false);
                            }

                            await SendBinaryAsync(ws, sendLock, pcm, pumpCts.Token).ConfigureAwait(false);
                        }
                        continue;
                    }

                    if (message.Type == RealtimeServerMessageType.OutputTextDelta && !string.IsNullOrEmpty(output.Text))
                    {
                        turnState.AppendText(output.Text);
                        await SendEventAsync(ws, sendLock, new TalkReply(output.Text), pumpCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (message.Type == RealtimeServerMessageType.OutputAudioTranscriptionDelta && !string.IsNullOrEmpty(output.Text))
                    {
                        if (turnState.AppendTranscript(output.Text))
                        {
                            await SendEventAsync(ws, sendLock, new TalkReply(output.Text), pumpCts.Token).ConfigureAwait(false);
                        }
                        continue;
                    }
                }

                if (message.Type == RealtimeServerMessageType.ResponseDone)
                {
                    var done = message as ResponseCreatedRealtimeServerMessage;
                    var allCalls = new List<FunctionCallContent>(pendingToolCalls);
                    pendingToolCalls.Clear();
                    if (done?.Items is { Count: > 0 })
                    {
                        allCalls.AddRange(done.Items
                            .Where(static i => i.Contents is { Count: > 0 })
                            .SelectMany(static i => i.Contents.OfType<FunctionCallContent>()));
                    }

                    if (allCalls.Count > 0)
                    {
                        var callsSnapshot = allCalls.ToArray();
                        activeTools.Start(pumpCts.Token, async turnCt =>
                        {
                            var executedAny = await HandleFunctionCallsAsync(
                                callsSnapshot,
                                handledToolCalls,
                                brainTurnCalls,
                                ws,
                                sendLock,
                                session,
                                conversation,
                                brains,
                                brainId,
                                turnState,
                                logger,
                                turnCt).ConfigureAwait(false);

                            if (executedAny && !turnCt.IsCancellationRequested)
                            {
                                await session.SendAsync(new CreateResponseRealtimeClientMessage(), turnCt).ConfigureAwait(false);
                            }
                        }, logger);
                        continue;
                    }

                    var (heard, reply, delegated) = turnState.Complete();
                    turnCounter++;

                    if (!talk.RecordsItself
                        && !delegated
                        && done?.Status != RealtimeResponseStatus.Cancelled
                        && !string.IsNullOrWhiteSpace(heard)
                        && !string.IsNullOrWhiteSpace(reply)
                        && talk.Model is { Value.Length: > 0 } model)
                    {
                        var responseId = !string.IsNullOrWhiteSpace(done?.ResponseId)
                            ? done.ResponseId
                            : $"voice-{turnCounter}";
                        try
                        {
                            await conversation.Voiced(heard, reply, model, responseId).WaitAsync(pumpCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            logger.LogSwallowed(error, "recording cloud voice exchange into conversation");
                            await SendEventAsync(ws, sendLock, new TalkError("Could not save the spoken exchange."), pumpCts.Token).ConfigureAwait(false);
                        }
                    }

                    await SendEventAsync(ws, sendLock, new TalkState("listening"), pumpCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            logger.LogSwallowed(error, "pumping realtime session messages to WebSocket client");
            await SendEventAsync(ws, sendLock, new TalkError(error.Message), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> HandleFunctionCallsAsync(
        IEnumerable<FunctionCallContent> calls,
        ConcurrentDictionary<string, byte> handledCallIds,
        BrainTurnCounter brainTurnCalls,
        WebSocket ws,
        SemaphoreSlim sendLock,
        IRealtimeClientSession session,
        IConversation conversation,
        IIntoChatOSBrains brains,
        string brainId,
        TurnAccumulator turnState,
        ILogger logger,
        CancellationToken ct)
    {
        var executedAny = false;
        foreach (var call in calls)
        {
            ct.ThrowIfCancellationRequested();
            var callId = string.IsNullOrWhiteSpace(call.CallId) ? Guid.NewGuid().ToString("N") : call.CallId;
            if (handledCallIds.ContainsKey(callId))
            {
                continue;
            }

            if (handledCallIds.Count < MaxHandledToolCallsPerSession && !handledCallIds.TryAdd(callId, 0))
            {
                continue;
            }

            executedAny = true;
            await SendEventAsync(ws, sendLock, new TalkState("thinking"), ct).ConfigureAwait(false);

            string resultText;
            if (string.Equals(call.Name, BrainTurnToolName, StringComparison.OrdinalIgnoreCase))
            {
                if (!brainTurnCalls.TryReserve(MaxBrainTurnCallsPerSession))
                {
                    resultText = "Voice session tool call limit reached. Ask the user to start a new voice session or continue in text chat.";
                }
                else
                {
                    var request = ExtractToolRequest(call, turnState.PeekHeard());
                    if (string.IsNullOrWhiteSpace(request))
                    {
                        resultText = "No request was provided for the brain turn.";
                    }
                    else
                    {
                        try
                        {
                            turnState.MarkDelegatedToBrain();
                            resultText = await ExecuteBrainTurnAsync(
                                conversation,
                                brains,
                                brainId,
                                request,
                                ct,
                                onToolEvent: (tool, token) => SendEventAsync(ws, sendLock, tool, token)).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            logger.LogSwallowed(error, "executing run_brain_turn from realtime voice session");
                            resultText = "I could not complete that action right now.";
                        }
                    }
                }
            }
            else
            {
                resultText = $"Tool '{call.Name}' is not available in voice mode. Use '{BrainTurnToolName}' to delegate OS actions.";
            }

            ct.ThrowIfCancellationRequested();
            await session.SendAsync(
                new CreateConversationItemRealtimeClientMessage(
                    new RealtimeConversationItem([new FunctionResultContent(callId, resultText)])),
                ct).ConfigureAwait(false);
        }

        return executedAny;
    }

    private static string? ExtractToolRequest(FunctionCallContent call, string? fallbackHeard)
    {
        if (call.Arguments is { Count: > 0 })
        {
            if (call.Arguments.TryGetValue("request", out var value) && value is not null)
            {
                var text = value is JsonElement json && json.ValueKind == JsonValueKind.String
                    ? json.GetString()
                    : value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            foreach (var entry in call.Arguments.Values)
            {
                if (entry is null) { continue; }
                var text = entry is JsonElement json && json.ValueKind == JsonValueKind.String
                    ? json.GetString()
                    : entry.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        return fallbackHeard;
    }

    private static bool IsBargeInSignal(RealtimeServerMessage message)
    {
        if (message.Type == LocalTalk.Interrupted)
        {
            return true;
        }

#pragma warning disable OPENAI002 // OpenAI.Realtime server VAD speech-started update type.
        return message.RawRepresentation is OpenAI.Realtime.RealtimeServerUpdateInputAudioBufferSpeechStarted;
#pragma warning restore OPENAI002
    }

    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveFrameAsync(
        WebSocket ws,
        byte[] buffer,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            var limit = result.MessageType == WebSocketMessageType.Binary ? MaxBinaryFrameBytes : MaxTextFrameBytes;
            if (ms.Length + result.Count > limit)
            {
                throw new InvalidDataException($"WebSocket frame exceeded the {limit}-byte limit.");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, ms.ToArray());
            }
        }
    }

    private static async Task SendEventAsync(WebSocket ws, SemaphoreSlim sendLock, TalkEvent ev, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(ev, TalkJson.Default.TalkEvent);
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task SendBinaryAsync(WebSocket ws, SemaphoreSlim sendLock, byte[] pcm, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
        {
            return;
        }

        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(pcm, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task CloseSafelyAsync(WebSocket ws, WebSocketCloseStatus status, string description)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        var safeDescription = TruncateUtf8(description, MaxCloseReasonBytes);
        try
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(status, safeDescription, closeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            OsConsoleLog.For<TalkEvent>().LogSwallowed(error, "closing talk WebSocket");
        }
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return text;
        }

        var length = maxBytes;
        while (length > 0 && (bytes[length - 1] & 0b1100_0000) == 0b1000_0000)
        {
            length--;
        }

        if (length > 0 && (bytes[length - 1] & 0b1000_0000) != 0)
        {
            length--;
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private sealed class TurnAccumulator
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _textReply = new();
        private readonly StringBuilder _transcriptReply = new();
        private string? _heard;
        private bool _speaking;
        private bool _delegatedToBrain;

        public void SetHeard(string heard)
        {
            lock (_gate)
            {
                _heard = heard;
                _textReply.Clear();
                _transcriptReply.Clear();
                _speaking = false;
                _delegatedToBrain = false;
            }
        }

        public string? PeekHeard()
        {
            lock (_gate)
            {
                return _heard;
            }
        }

        public void MarkDelegatedToBrain()
        {
            lock (_gate)
            {
                _delegatedToBrain = true;
            }
        }

        public bool MarkSpeaking()
        {
            lock (_gate)
            {
                if (_speaking) { return false; }
                _speaking = true;
                return true;
            }
        }

        public void AppendText(string delta)
        {
            lock (_gate)
            {
                _textReply.Append(delta);
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
                var heard = _heard;
                var reply = (_textReply.Length > 0 ? _textReply : _transcriptReply).ToString().Trim();
                var delegated = _delegatedToBrain;
                _heard = null;
                _textReply.Clear();
                _transcriptReply.Clear();
                _speaking = false;
                _delegatedToBrain = false;
                return (heard, reply, delegated);
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _heard = null;
                _textReply.Clear();
                _transcriptReply.Clear();
                _speaking = false;
                _delegatedToBrain = false;
            }
        }
    }

    private sealed class BrainTurnCounter
    {
        private int _count;

        public bool TryReserve(int max)
        {
            while (true)
            {
                var current = Volatile.Read(ref _count);
                if (current >= max)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _count, current + 1, current) == current)
                {
                    return true;
                }
            }
        }
    }

    private sealed class ActiveToolExecution
    {
        private readonly Lock _gate = new();
        private CancellationTokenSource? _currentCts;
        private Task? _currentTask;

        public void Start(CancellationToken sessionCt, Func<CancellationToken, Task> work, ILogger logger)
        {
            CancellationTokenSource? priorCts;
            var nextCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
            lock (_gate)
            {
                priorCts = _currentCts;
                _currentCts = nextCts;
                _currentTask = Task.Run(async () =>
                {
                    try
                    {
                        await work(nextCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception error)
                    {
                        logger.LogSwallowed(error, "running background voice tool execution");
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_currentCts, nextCts))
                            {
                                _currentCts = null;
                            }
                        }

                        nextCts.Dispose();
                    }
                });
            }

            CancelSafe(priorCts);
        }

        public void CancelActive()
        {
            CancellationTokenSource? toCancel;
            lock (_gate)
            {
                toCancel = _currentCts;
                _currentCts = null;
            }

            CancelSafe(toCancel);
        }

        public async Task CancelAndDrainAsync()
        {
            CancellationTokenSource? toCancel;
            Task? toAwait;
            lock (_gate)
            {
                toCancel = _currentCts;
                _currentCts = null;
                toAwait = _currentTask;
                _currentTask = null;
            }

            CancelSafe(toCancel);
            if (toAwait is not null)
            {
                try
                {
                    await toAwait.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private static void CancelSafe(CancellationTokenSource? cts)
        {
            if (cts is null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException error)
            {
                OsConsoleLog.For<TalkEvent>().LogSwallowed(error, "cancelling already-disposed voice tool token");
            }
        }
    }
}
