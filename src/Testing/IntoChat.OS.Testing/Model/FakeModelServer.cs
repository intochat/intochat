using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Testing.Model;

// The model behind the real OpenAI adapter in tests. Replies are scripted per request model id;
// a model nobody scripted echoes, so hosts that only need "a model answered" script nothing.
public sealed class FakeModelServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<FakeReply>> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<JsonElement>> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _answered = new(StringComparer.Ordinal);

    private FakeModelServer(WebApplication app) { _app = app; }

    public Uri Endpoint => new(new Uri(_app.Urls.Single()), "/v1/");

    public static async Task<FakeModelServer> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeModelServer(app);
        app.MapPost("/v1/chat/completions", (Delegate)server.Respond);
        app.MapPost("/v1/systemone", (Delegate)server.RespondSystemOne);
        await app.StartAsync(ct);
        return server;
    }

    // A host that routes through Jev points its `typesafe` integration here. Unset answers a
    // choice question deterministically (prefer "timer", then "Start", else the first option).
    public Func<JsonElement, string>? SystemOneOverride { get; set; }

    // A fallback for chat requests no model script matched; null keeps the echo default.
    public Func<JsonElement, FakeReply?>? ChatFallback { get; set; }

    public void Script(string modelId, params FakeReply[] replies)
    {
        var queue = _scripts.GetOrAdd(modelId, _ => new());
        foreach (var reply in replies) { queue.Enqueue(reply); }
    }

    public IReadOnlyList<JsonElement> Requests(string modelId)
        => _requests.TryGetValue(modelId, out var requests) ? [.. requests] : [];

    private async Task<IResult> Respond(HttpContext http)
    {
        using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var request = document.RootElement.Clone();
        var model = request.GetProperty("model").GetString() ?? "";
        _requests.GetOrAdd(model, _ => new()).Enqueue(request);

        FakeReply reply;
        if (!_scripts.TryGetValue(model, out var script)) { reply = ChatFallback?.Invoke(request) ?? FakeReply.Text("You said: " + LastUserText(request)); }
        else if (!script.TryDequeue(out reply!))
        { return Results.Problem($"Fake model '{model}' ran out of replies after {_answered.GetValueOrDefault(model)}.", statusCode: 500); }
        _answered.AddOrUpdate(model, 1, (_, count) => count + 1);

        var streaming = request.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
        return streaming ? Stream(model, reply) : Complete(model, reply);
    }

    private async Task<IResult> RespondSystemOne(HttpContext http)
    {
        using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var request = document.RootElement.Clone();
        var body = SystemOneOverride?.Invoke(request) ?? DefaultSystemOne(request);
        return Results.Text(body, "application/json");
    }

    private static string DefaultSystemOne(JsonElement request)
    {
        var answers = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var question in request.GetProperty("questions").EnumerateObject())
        {
            var type = question.Value.GetProperty("type").GetString();
            if (type == "choice")
            {
                var options = question.Value.GetProperty("criteria").EnumerateObject().Select(option => option.Name).ToArray();
                var pick = options.FirstOrDefault(option => option.Equals("timer", StringComparison.OrdinalIgnoreCase))
                    ?? options.FirstOrDefault(option => option.Equals("Start", StringComparison.OrdinalIgnoreCase))
                    ?? options.FirstOrDefault() ?? "";
                var probabilities = options.ToDictionary(option => option, option => option == pick ? 0.95 : (options.Length > 1 ? 0.05 / (options.Length - 1) : 0.0));
                answers[question.Name] = new { type = "choice", choice = pick, probabilities, confidence = 0.95 };
            }
            else if (type == "noul") { answers[question.Name] = new { type = "noul", noul = 1.0 }; }
            else { answers[question.Name] = new { type = "score", score = 0, probabilities = new Dictionary<string, double>(), confidence = 1.0 }; }
        }
        return JsonSerializer.Serialize(new { model = "fake-jev", answers });
    }

    private static string LastUserText(JsonElement request)
    {
        var user = request.GetProperty("messages").EnumerateArray().LastOrDefault(m => m.GetProperty("role").GetString() == "user");
        if (user.ValueKind != JsonValueKind.Object) { return ""; }
        var content = user.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString() ?? ""
            : string.Concat(content.EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString()));
    }

    private static readonly object Usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 };

    private static IResult Complete(string model, FakeReply reply)
    {
        var (message, finish) = reply switch
        {
            FakeReply.ToolReply tool => ((object)new { role = "assistant", content = (string?)null, tool_calls = new[] { ToolCall(tool) } }, "tool_calls"),
            FakeReply.TextReply text => (new { role = "assistant", content = text.Content }, "stop"),
            _ => throw new InvalidOperationException(reply.GetType().Name),
        };
        return Results.Json(new { id = "fake", @object = "chat.completion", created = 1, model, choices = new[] { new { index = 0, message, finish_reason = finish } }, usage = Usage });
    }

    // Delta, finish, then the usage chunk the SDK reads token counts from.
    private static IResult Stream(string model, FakeReply reply)
    {
        object Chunk(object[] choices, object? usage) => new { id = "fake", @object = "chat.completion.chunk", created = 1, model, choices, usage };
        var (delta, finish) = reply switch
        {
            FakeReply.ToolReply tool => ((object)new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call_fake", type = "function", function = new { name = tool.Name, arguments = tool.ArgumentsJson } } } }, "tool_calls"),
            FakeReply.TextReply text => (new { role = "assistant", content = text.Content }, "stop"),
            _ => throw new InvalidOperationException(reply.GetType().Name),
        };
        var events = new[]
        {
            Chunk([new { index = 0, delta, finish_reason = (string?)null }], null),
            Chunk([new { index = 0, delta = new { }, finish_reason = (string?)finish }], null),
            Chunk([], Usage),
        };
        var body = string.Concat(events.Select(chunk => "data: " + JsonSerializer.Serialize(chunk) + "\n\n")) + "data: [DONE]\n\n";
        return Results.Text(body, "text/event-stream");
    }

    private static object ToolCall(FakeReply.ToolReply tool)
        => new { id = "call_fake", type = "function", function = new { name = tool.Name, arguments = tool.ArgumentsJson } };

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
