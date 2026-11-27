using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Voice;

internal sealed class GeminiLiveRealtimeClient : IRealtimeClient
{
    internal static readonly Uri DefaultEndpoint =
        new("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");

    internal const int InputSampleRate = 16_000;
    internal const int OutputSampleRate = 24_000;

    private readonly string _modelId;
    private readonly string _apiKey;
    private readonly Uri _endpoint;
    private readonly Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<WebSocket>> _socketFactory;

    public GeminiLiveRealtimeClient(
        string modelId,
        string apiKey,
        Func<Uri, IReadOnlyDictionary<string, string>, CancellationToken, Task<WebSocket>>? socketFactory = null,
        Uri? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _modelId = modelId.Trim();
        _apiKey = apiKey.Trim();
        _endpoint = endpoint ?? DefaultEndpoint;
        _socketFactory = socketFactory ?? DefaultConnectAsync;
    }

    public async Task<IRealtimeClientSession> CreateSessionAsync(
        RealtimeSessionOptions? sessionOptions = null,
        CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-goog-api-key"] = _apiKey,
        };

        var socket = await _socketFactory(_endpoint, headers, cancellationToken).ConfigureAwait(false);
        try
        {
            var session = new GeminiLiveRealtimeSession(socket, _modelId, sessionOptions);
            await session.SendSetupAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose() { }

    private static async Task<WebSocket> DefaultConnectAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        foreach (var (name, value) in headers)
        {
            ws.Options.SetRequestHeader(name, value);
        }

        try
        {
            await ws.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }
}

internal sealed class GeminiLiveRealtimeSession : IRealtimeClientSession
{
    private const int MaxServerFrameBytes = 4 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly string _modelId;
    private readonly RealtimeSessionOptions? _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<RealtimeServerMessage> _output = Channel.CreateUnbounded<RealtimeServerMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, string> _callNames = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly Task _receiveTask;
    private int _disposed;
    private long _turnCounter;

    internal GeminiLiveRealtimeSession(WebSocket socket, string modelId, RealtimeSessionOptions? options)
    {
        _socket = socket;
        _modelId = modelId;
        _options = options;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public RealtimeSessionOptions? Options => _options;

    internal Task SendSetupAsync(CancellationToken ct)
    {
        var payload = BuildSetupJson(_modelId, _options);
        return SendJsonAsync(payload, ct);
    }

    public async Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        switch (message)
        {
            case InputAudioBufferAppendRealtimeClientMessage append when !append.Content.Data.IsEmpty:
                var base64 = Convert.ToBase64String(append.Content.Data.Span);
                var audioJson = BuildRealtimeAudioJson(base64, _options?.InputAudioFormat?.SampleRate ?? GeminiLiveRealtimeClient.InputSampleRate);
                await SendJsonAsync(audioJson, cancellationToken).ConfigureAwait(false);
                return;

            case CreateConversationItemRealtimeClientMessage item when item.Item?.Contents is { Count: > 0 }:
                var results = item.Item.Contents.OfType<FunctionResultContent>().ToArray();
                if (results.Length > 0)
                {
                    var responseJson = BuildToolResponseJson(results, _callNames);
                    await SendJsonAsync(responseJson, cancellationToken).ConfigureAwait(false);
                }
                return;

            case CreateResponseRealtimeClientMessage:
                // Gemini Live automatically continues generation after receiving toolResponse.
                return;

#pragma warning disable OPENAI002 // OpenAI.Realtime cancel command forwarded by TalkEndpoint on tap-to-interrupt.
            case RealtimeClientMessage { RawRepresentation: OpenAI.Realtime.RealtimeClientCommandResponseCancel }:
                _output.Writer.TryWrite(new RealtimeServerMessage { Type = LocalTalk.Interrupted });
                return;
#pragma warning restore OPENAI002
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _sessionCts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended.", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            OsConsoleLog.For<GeminiLiveRealtimeSession>().LogSwallowed(error, "closing Gemini Live WebSocket");
        }

        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _output.Writer.TryComplete();
        _socket.Dispose();
        _sendLock.Dispose();
        _sessionCts.Dispose();
    }

    private async Task SendJsonAsync(string json, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (!_sessionCts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _sessionCts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (ms.Length + result.Count > MaxServerFrameBytes)
                    {
                        throw new InvalidDataException("Gemini Live frame exceeded maximum allowed size.");
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (ms.Length == 0)
                {
                    continue;
                }

                HandleServerFrame(ms.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            OsConsoleLog.For<GeminiLiveRealtimeSession>().LogSwallowed(error, "receiving Gemini Live server frame");
            _output.Writer.TryWrite(new ErrorRealtimeServerMessage
            {
                Type = RealtimeServerMessageType.Error,
                Error = new ErrorContent(error.Message),
            });
        }
        finally
        {
            _output.Writer.TryComplete();
        }
    }

    internal void HandleServerFrame(ReadOnlyMemory<byte> utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;

        if (root.TryGetProperty("serverContent", out var serverContent) && serverContent.ValueKind == JsonValueKind.Object)
        {
            if (serverContent.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True)
            {
                _output.Writer.TryWrite(new RealtimeServerMessage { Type = LocalTalk.Interrupted });
            }

            if (serverContent.TryGetProperty("inputTranscription", out var inputTrans)
                && inputTrans.TryGetProperty("text", out var inputText)
                && inputText.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(inputText.GetString()))
            {
                _output.Writer.TryWrite(new InputAudioTranscriptionRealtimeServerMessage(
                    RealtimeServerMessageType.InputAudioTranscriptionCompleted)
                {
                    Transcription = inputText.GetString()!.Trim(),
                });
            }

            if (serverContent.TryGetProperty("outputTranscription", out var outputTrans)
                && outputTrans.TryGetProperty("text", out var outputText)
                && outputText.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(outputText.GetString()))
            {
                _output.Writer.TryWrite(new OutputTextAudioRealtimeServerMessage(
                    RealtimeServerMessageType.OutputAudioTranscriptionDelta)
                {
                    Text = outputText.GetString()!,
                });
            }

            if (serverContent.TryGetProperty("modelTurn", out var modelTurn)
                && modelTurn.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if ((part.TryGetProperty("inlineData", out var inlineData) || part.TryGetProperty("inline_data", out inlineData))
                        && inlineData.TryGetProperty("data", out var dataProp)
                        && dataProp.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(dataProp.GetString()))
                    {
                        _output.Writer.TryWrite(new OutputTextAudioRealtimeServerMessage(
                            RealtimeServerMessageType.OutputAudioDelta)
                        {
                            Audio = dataProp.GetString()!,
                        });
                    }

                    if (part.TryGetProperty("text", out var partText)
                        && partText.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(partText.GetString()))
                    {
                        _output.Writer.TryWrite(new OutputTextAudioRealtimeServerMessage(
                            RealtimeServerMessageType.OutputTextDelta)
                        {
                            Text = partText.GetString()!,
                        });
                    }
                }
            }

            if (serverContent.TryGetProperty("turnComplete", out var turnComplete) && turnComplete.ValueKind == JsonValueKind.True)
            {
                var turnId = Interlocked.Increment(ref _turnCounter);
                _output.Writer.TryWrite(new ResponseCreatedRealtimeServerMessage(
                    RealtimeServerMessageType.ResponseDone)
                {
                    ResponseId = $"gemini-{turnId}",
                    Status = RealtimeResponseStatus.Completed,
                });
            }
        }

        if (root.TryGetProperty("toolCall", out var toolCall)
            && toolCall.TryGetProperty("functionCalls", out var functionCalls)
            && functionCalls.ValueKind == JsonValueKind.Array)
        {
            var calls = new List<AIContent>();
            foreach (var call in functionCalls.EnumerateArray())
            {
                var name = call.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var callId = call.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idProp.GetString())
                    ? idProp.GetString()!
                    : Guid.NewGuid().ToString("N");
                _callNames[callId] = name;

                Dictionary<string, object?>? args = null;
                if (call.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
                {
                    args = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var prop in argsProp.EnumerateObject())
                    {
                        args[prop.Name] = prop.Value.Clone();
                    }
                }

                calls.Add(new FunctionCallContent(callId, name, args));
            }

            if (calls.Count > 0)
            {
                var turnId = Interlocked.Increment(ref _turnCounter);
                var responseId = $"gemini-tool-{turnId}";
                _output.Writer.TryWrite(new ResponseOutputItemRealtimeServerMessage(
                    RealtimeServerMessageType.ResponseOutputItemDone)
                {
                    ResponseId = responseId,
                    Item = new RealtimeConversationItem(calls),
                });
                _output.Writer.TryWrite(new ResponseCreatedRealtimeServerMessage(
                    RealtimeServerMessageType.ResponseDone)
                {
                    ResponseId = responseId,
                    Status = RealtimeResponseStatus.Completed,
                });
            }
        }
    }

    internal static string BuildSetupJson(string modelId, RealtimeSessionOptions? options)
    {
        var fullModel = modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? modelId
            : $"models/{modelId}";
        var voice = GeminiTextToSpeechClient.ResolveGeminiVoice(options?.Voice);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("setup");
            writer.WriteStartObject();
            writer.WriteString("model", fullModel);

            writer.WritePropertyName("generationConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("responseModalities");
            writer.WriteStartArray();
            writer.WriteStringValue("AUDIO");
            writer.WriteEndArray();
            writer.WritePropertyName("speechConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("voiceConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("prebuiltVoiceConfig");
            writer.WriteStartObject();
            writer.WriteString("voiceName", voice);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WritePropertyName("inputAudioTranscription");
            writer.WriteStartObject();
            writer.WriteEndObject();

            writer.WritePropertyName("outputAudioTranscription");
            writer.WriteStartObject();
            writer.WriteEndObject();

            if (!string.IsNullOrWhiteSpace(options?.Instructions))
            {
                writer.WritePropertyName("systemInstruction");
                writer.WriteStartObject();
                writer.WritePropertyName("parts");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("text", options.Instructions);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            if (options?.Tools is { Count: > 0 } tools)
            {
                var declarations = tools.OfType<AIFunctionDeclaration>().ToArray();
                if (declarations.Length > 0)
                {
                    writer.WritePropertyName("tools");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WritePropertyName("functionDeclarations");
                    writer.WriteStartArray();
                    foreach (var fn in declarations)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", fn.Name);
                        if (!string.IsNullOrWhiteSpace(fn.Description))
                        {
                            writer.WriteString("description", fn.Description);
                        }

                        if (fn.JsonSchema.ValueKind == JsonValueKind.Object)
                        {
                            writer.WritePropertyName("parameters");
                            fn.JsonSchema.WriteTo(writer);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string BuildRealtimeAudioJson(string base64Pcm, int sampleRate)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("realtimeInput");
            writer.WriteStartObject();
            writer.WritePropertyName("audio");
            writer.WriteStartObject();
            writer.WriteString("mimeType", $"audio/pcm;rate={sampleRate}");
            writer.WriteString("data", base64Pcm);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string BuildToolResponseJson(
        IReadOnlyList<FunctionResultContent> results,
        ConcurrentDictionary<string, string> callNames)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("toolResponse");
            writer.WriteStartObject();
            writer.WritePropertyName("functionResponses");
            writer.WriteStartArray();
            foreach (var result in results)
            {
                var callId = result.CallId ?? string.Empty;
                var name = callNames.TryRemove(callId, out var resolvedName) ? resolvedName : TalkEndpoint.BrainTurnToolName;
                writer.WriteStartObject();
                writer.WriteString("id", callId);
                writer.WriteString("name", name);
                writer.WritePropertyName("response");
                writer.WriteStartObject();
                writer.WriteString("result", result.Result?.ToString() ?? string.Empty);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
