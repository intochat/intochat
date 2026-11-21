using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IntoChat.OS.Tests.Support;

public sealed record ScriptedOllamaChatMessage(
    string Role,
    string Content,
    string[] ToolCalls);

public sealed record ScriptedOllamaChatRequest(
    string Model,
    bool Stream,
    ScriptedOllamaChatMessage[] Messages,
    string[] Tools,
    string RawJson)
{
    public bool HasSystemMessageContaining(string text)
        => Messages.Any(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)
            && m.Content.Contains(text, StringComparison.OrdinalIgnoreCase));

    public bool HasMessageContaining(string role, string text)
        => Messages.Any(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase)
            && m.Content.Contains(text, StringComparison.OrdinalIgnoreCase));
}

public abstract record ScriptedOllamaOutcome
{
    private ScriptedOllamaOutcome() { }

    public sealed record Reply(string Text, string? Model = null) : ScriptedOllamaOutcome;

    public sealed record ToolCall(string ToolName, JsonElement Arguments, string? Text = null, string? Model = null) : ScriptedOllamaOutcome;

    public sealed record Failure(HttpStatusCode StatusCode, string Message) : ScriptedOllamaOutcome;

    internal sealed record Held(
        TaskCompletionSource Entered,
        Task<ScriptedOllamaOutcome> Release) : ScriptedOllamaOutcome;
}

public sealed record HeldOllamaTurn(Task Entered, Action<string> ReleaseWithReply, Action<HttpStatusCode, string> ReleaseWithFailure)
{
    public void Release(string reply) => ReleaseWithReply(reply);
}

/// <summary>
/// Real HTTP/1.1 TCP socket fake for Ollama (<c>/api/tags</c>, <c>/api/show</c>, <c>/api/embed</c>, <c>/api/chat</c>).
/// Runs over loopback sockets so the product host executes its genuine <c>OllamaRuntime</c> / <c>OllamaApiClient</c>
/// HTTP + NDJSON pipeline with zero DI test doubles.
/// </summary>
public sealed class ScriptedOllama : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<ScriptedOllamaOutcome> _queue = new();
    private readonly ConcurrentQueue<ScriptedOllamaChatRequest> _chatRequests = new();
    private readonly ConcurrentQueue<string> _requestedPaths = new();
    private readonly ConcurrentBag<Task> _inflight = [];
    private volatile Func<ScriptedOllamaChatRequest, ScriptedOllamaOutcome>? _responder;
    private int _disposed;

    private ScriptedOllama(TcpListener listener, string[] models)
    {
        _listener = listener;
        Models = models;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        BaseAddress = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
        _acceptLoop = Task.Run(AcceptConnectionsAsync);
    }

    public Uri BaseAddress { get; }

    public string Endpoint => BaseAddress.GetLeftPart(UriPartial.Authority);

    public string[] Models { get; set; }

    public IReadOnlyList<ScriptedOllamaChatRequest> ChatRequests => [.. _chatRequests];

    public IReadOnlyList<string> RequestedPaths => [.. _requestedPaths];

    public static ScriptedOllama Start(params string[] models)
    {
        var effectiveModels = models.Length > 0 ? models : ["qwen3.5:4b", "nomic-embed-text:latest"];
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ScriptedOllama(listener, effectiveModels);
    }

    public void ClearRequests()
    {
        while (_chatRequests.TryDequeue(out _)) { }
        while (_requestedPaths.TryDequeue(out _)) { }
    }

    public void ClearResponses()
    {
        while (_queue.TryDequeue(out _)) { }
        _responder = null;
    }

    public void EnqueueReply(string text, string? model = null)
        => _queue.Enqueue(new ScriptedOllamaOutcome.Reply(text, model));

    public void EnqueueToolCall(string toolName, object arguments, string? text = null, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var argsElement = JsonSerializer.SerializeToElement(arguments, JsonSerializerOptions.Web);
        _queue.Enqueue(new ScriptedOllamaOutcome.ToolCall(toolName, argsElement, text, model));
    }

    public void EnqueueFailure(HttpStatusCode statusCode, string message = "Scripted Ollama failure")
        => _queue.Enqueue(new ScriptedOllamaOutcome.Failure(statusCode, message));

    public HeldOllamaTurn HoldNext(string? model = null)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ScriptedOllamaOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new ScriptedOllamaOutcome.Held(entered, release.Task));
        return new HeldOllamaTurn(
            entered.Task,
            reply => release.TrySetResult(new ScriptedOllamaOutcome.Reply(reply, model)),
            (code, msg) => release.TrySetResult(new ScriptedOllamaOutcome.Failure(code, msg)));
    }

    public void Respond(Func<ScriptedOllamaChatRequest, ScriptedOllamaOutcome> responder)
        => _responder = responder ?? throw new ArgumentNullException(nameof(responder));

    public void RespondWithReply(string text, string? model = null)
        => _responder = _ => new ScriptedOllamaOutcome.Reply(text, model);

    private async Task AcceptConnectionsAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            var task = Task.Run(() => HandleClientAsync(client, _shutdown.Token));
            _inflight.Add(task);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await ReadHttpRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                _requestedPaths.Enqueue($"{request.Method} {request.Path}");

                if (string.Equals(request.Path, "/", StringComparison.Ordinal))
                {
                    await WritePlainResponseAsync(stream, HttpStatusCode.OK, "text/plain", "Ollama is running", close: true, ct).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request.Path, "/api/tags", StringComparison.Ordinal))
                {
                    var tagsJson = BuildTagsResponse();
                    await WritePlainResponseAsync(stream, HttpStatusCode.OK, "application/json", tagsJson, close: true, ct).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request.Path, "/api/show", StringComparison.Ordinal))
                {
                    const string showJson = """{"modelfile":"","parameters":"","template":"","capabilities":["completion","tools"],"details":{"family":"qwen35","parameter_size":"4B"}}""";
                    await WritePlainResponseAsync(stream, HttpStatusCode.OK, "application/json", showJson, close: true, ct).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request.Path, "/api/embed", StringComparison.Ordinal))
                {
                    var vector = Enumerable.Repeat(0.05f, 384).ToArray();
                    var embedJson = JsonSerializer.Serialize(new { model = "nomic-embed-text", embeddings = new[] { vector } });
                    await WritePlainResponseAsync(stream, HttpStatusCode.OK, "application/json", embedJson, close: true, ct).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request.Path, "/api/embeddings", StringComparison.Ordinal))
                {
                    var vector = Enumerable.Repeat(0.05f, 384).ToArray();
                    var embedJson = JsonSerializer.Serialize(new { embedding = vector });
                    await WritePlainResponseAsync(stream, HttpStatusCode.OK, "application/json", embedJson, close: true, ct).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request.Path, "/api/chat", StringComparison.Ordinal))
                {
                    var parsed = ParseChatRequest(request.Body);
                    _chatRequests.Enqueue(parsed);

                    var outcome = ResolveOutcome(parsed);
                    if (outcome is ScriptedOllamaOutcome.Held held)
                    {
                        held.Entered.TrySetResult();
                        outcome = await held.Release.WaitAsync(ct).ConfigureAwait(false);
                    }

                    await WriteChatOutcomeAsync(stream, parsed, outcome, ct).ConfigureAwait(false);
                    return;
                }

                await WritePlainResponseAsync(stream, HttpStatusCode.NotFound, "application/json", """{"error":"unknown endpoint"}""", close: true, ct).ConfigureAwait(false);
                return;
            }
        }
    }

    private ScriptedOllamaOutcome ResolveOutcome(ScriptedOllamaChatRequest request)
    {
        if (_queue.TryDequeue(out var queued))
        {
            return queued;
        }

        if (_responder is { } responder)
        {
            return responder(request);
        }

        return new ScriptedOllamaOutcome.Reply("ok");
    }

    private string BuildTagsResponse()
    {
        var items = Models.Select(model => new
        {
            name = model,
            model,
            modified_at = "2026-11-16T00:00:00Z",
            size = 1024L,
            digest = "sha256:scripted",
            details = new
            {
                family = "qwen35",
                parameter_size = "4B",
            },
        });
        return JsonSerializer.Serialize(new { models = items });
    }

    private static ScriptedOllamaChatRequest ParseChatRequest(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new ScriptedOllamaChatRequest("qwen3.5:4b", true, [], [], "{}");
        }

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "qwen3.5:4b"
            : "qwen3.5:4b";
        var stream = !root.TryGetProperty("stream", out var s) || s.ValueKind != JsonValueKind.False;

        var messages = new List<ScriptedOllamaChatMessage>();
        if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in msgs.EnumerateArray())
            {
                var role = item.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
                var content = item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                var calls = new List<string>();
                if (item.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        if (tc.TryGetProperty("function", out var fn)
                            && fn.TryGetProperty("name", out var fnName)
                            && fnName.ValueKind == JsonValueKind.String)
                        {
                            calls.Add(fnName.GetString()!);
                        }
                    }
                }

                messages.Add(new ScriptedOllamaChatMessage(role, content, [.. calls]));
            }
        }

        var tools = new List<string>();
        if (root.TryGetProperty("tools", out var toolsElem) && toolsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in toolsElem.EnumerateArray())
            {
                if (item.TryGetProperty("function", out var fn)
                    && fn.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    tools.Add(name.GetString()!);
                }
            }
        }

        return new ScriptedOllamaChatRequest(model, stream, [.. messages], [.. tools], rawJson);
    }

    private static async Task WriteChatOutcomeAsync(
        NetworkStream stream,
        ScriptedOllamaChatRequest request,
        ScriptedOllamaOutcome outcome,
        CancellationToken ct)
    {
        if (outcome is ScriptedOllamaOutcome.Failure failure)
        {
            var errJson = JsonSerializer.Serialize(new { error = failure.Message });
            await WritePlainResponseAsync(stream, failure.StatusCode, "application/json", errJson, close: true, ct).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (!request.Stream)
        {
            var single = BuildFinalSingleChatChunk(request.Model, now, outcome);
            await WritePlainResponseAsync(stream, HttpStatusCode.OK, "application/json", single, close: true, ct).ConfigureAwait(false);
            return;
        }

        var header = "HTTP/1.1 200 OK\r\nContent-Type: application/x-ndjson\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);

        switch (outcome)
        {
            case ScriptedOllamaOutcome.Reply reply:
                {
                    var effectiveModel = reply.Model ?? request.Model;
                    var contentChunk = BuildContentChunk(effectiveModel, now, reply.Text, done: false);
                    await WriteChunkLineAsync(stream, contentChunk, ct).ConfigureAwait(false);
                    var doneChunk = BuildDoneChunk(effectiveModel, now);
                    await WriteChunkLineAsync(stream, doneChunk, ct).ConfigureAwait(false);
                    break;
                }

            case ScriptedOllamaOutcome.ToolCall toolCall:
                {
                    var effectiveModel = toolCall.Model ?? request.Model;
                    var callChunk = BuildToolCallChunk(effectiveModel, now, toolCall.ToolName, toolCall.Arguments, toolCall.Text, done: false);
                    await WriteChunkLineAsync(stream, callChunk, ct).ConfigureAwait(false);
                    var doneChunk = BuildDoneChunk(effectiveModel, now);
                    await WriteChunkLineAsync(stream, doneChunk, ct).ConfigureAwait(false);
                    break;
                }
        }

        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string BuildContentChunk(string model, string createdAt, string content, bool done)
    {
        var obj = new JsonObject
        {
            ["model"] = model,
            ["created_at"] = createdAt,
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content,
            },
            ["done"] = done,
        };
        return obj.ToJsonString();
    }

    private static string BuildToolCallChunk(
        string model,
        string createdAt,
        string toolName,
        JsonElement arguments,
        string? text,
        bool done)
    {
        var argsNode = JsonNode.Parse(arguments.GetRawText()) ?? new JsonObject();
        var callId = "call_" + Guid.NewGuid().ToString("N")[..12];
        var obj = new JsonObject
        {
            ["model"] = model,
            ["created_at"] = createdAt,
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text ?? string.Empty,
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = callId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["index"] = 0,
                            ["name"] = toolName,
                            ["arguments"] = argsNode,
                        },
                    },
                },
            },
            ["done"] = done,
        };
        return obj.ToJsonString();
    }

    private static string BuildDoneChunk(string model, string createdAt)
    {
        var obj = new JsonObject
        {
            ["model"] = model,
            ["created_at"] = createdAt,
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
            },
            ["done"] = true,
            ["done_reason"] = "stop",
            ["prompt_eval_count"] = 16,
            ["eval_count"] = 8,
        };
        return obj.ToJsonString();
    }

    private static string BuildFinalSingleChatChunk(string model, string createdAt, ScriptedOllamaOutcome outcome)
    {
        return outcome switch
        {
            ScriptedOllamaOutcome.Reply reply => new JsonObject
            {
                ["model"] = reply.Model ?? model,
                ["created_at"] = createdAt,
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = reply.Text,
                },
                ["done"] = true,
                ["done_reason"] = "stop",
                ["prompt_eval_count"] = 16,
                ["eval_count"] = 8,
            }.ToJsonString(),

            ScriptedOllamaOutcome.ToolCall toolCall => BuildToolCallChunk(
                toolCall.Model ?? model,
                createdAt,
                toolCall.ToolName,
                toolCall.Arguments,
                toolCall.Text,
                done: true),

            _ => BuildDoneChunk(model, createdAt),
        };
    }

    private static async Task WriteChunkLineAsync(NetworkStream stream, string jsonLine, CancellationToken ct)
    {
        var lineBytes = Encoding.UTF8.GetBytes(jsonLine + "\n");
        var sizeHeader = Encoding.ASCII.GetBytes(lineBytes.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
        await stream.WriteAsync(sizeHeader, ct).ConfigureAwait(false);
        await stream.WriteAsync(lineBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WritePlainResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string contentType,
        string body,
        bool close,
        CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var reason = ReasonPhrase(statusCode);
        var conn = close ? "close" : "keep-alive";
        var headers = $"HTTP/1.1 {(int)statusCode} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: {conn}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string ReasonPhrase(HttpStatusCode code) => code switch
    {
        HttpStatusCode.OK => "OK",
        HttpStatusCode.Unauthorized => "Unauthorized",
        HttpStatusCode.Forbidden => "Forbidden",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.TooManyRequests => "Too Many Requests",
        HttpStatusCode.InternalServerError => "Internal Server Error",
        HttpStatusCode.ServiceUnavailable => "Service Unavailable",
        _ => code.ToString(),
    };

    private static async Task<RawHttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBuffer = new MemoryStream();
        var single = new byte[1];
        var tail = 0;

        while (true)
        {
            var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var b = single[0];
            headerBuffer.WriteByte(b);
            tail = (tail << 8) | b;
            if (tail == 0x0D0A0D0A)
            {
                break;
            }

            if (headerBuffer.Length > 64 * 1024)
            {
                throw new InvalidDataException("HTTP request headers exceeded 64 KiB.");
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBuffer.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 2)
        {
            return null;
        }

        var method = parts[0].Trim().ToUpperInvariant();
        var rawTarget = parts[1].Trim();
        var queryIdx = rawTarget.IndexOf('?');
        var path = queryIdx >= 0 ? rawTarget[..queryIdx] : rawTarget;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        if (headers.TryGetValue("Expect", out var expect)
            && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        }

        string body = string.Empty;
        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
            && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedBodyAsync(stream, ct).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Content-Length", out var lengthRaw)
            && int.TryParse(lengthRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contentLength)
            && contentLength > 0)
        {
            var bodyBytes = new byte[contentLength];
            await stream.ReadExactlyAsync(bodyBytes, ct).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(bodyBytes);
        }

        return new RawHttpRequest(method, path, body);
    }

    private static async Task<string> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken ct)
    {
        var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (sizeLine is null)
            {
                break;
            }

            var semi = sizeLine.IndexOf(';');
            var hex = semi >= 0 ? sizeLine[..semi].Trim() : sizeLine.Trim();
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
            {
                throw new InvalidDataException($"Invalid HTTP chunk size '{sizeLine}'.");
            }

            if (chunkSize == 0)
            {
                while (true)
                {
                    var trailer = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(trailer))
                    {
                        break;
                    }
                }
                break;
            }

            var chunk = new byte[chunkSize];
            await stream.ReadExactlyAsync(chunk, ct).ConfigureAwait(false);
            body.Write(chunk, 0, chunk.Length);
            _ = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var line = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
            }

            if (single[0] == (byte)'\n')
            {
                var bytes = line.ToArray();
                var len = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                return Encoding.ASCII.GetString(bytes, 0, len);
            }

            line.WriteByte(single[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var task in _inflight)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _shutdown.Dispose();
    }

    private sealed record RawHttpRequest(string Method, string Path, string Body);
}
