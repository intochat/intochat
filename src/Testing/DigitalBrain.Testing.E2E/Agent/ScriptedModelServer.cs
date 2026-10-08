using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Testing.E2E.Agent;

// Exercises the production OpenAI adapter. This fixture owns no application services.
public sealed class ScriptedModelServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<JsonElement> _requests = new();
    public ConcurrentQueue<string> Errors { get; } = new();

    // One server outlives the shared host; every fact starts from no requests and no errors.
    public void Reset()
    {
        _requests.Clear();
        Errors.Clear();
    }
    private ScriptedModelServer(WebApplication app) { _app = app; }
    public Uri Endpoint => new(new Uri(_app.Urls.Single()), "/v1/");
    public IReadOnlyList<JsonElement> Requests => _requests.ToArray();
    public static async Task<ScriptedModelServer> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new ScriptedModelServer(app);
        app.MapPost("/v1/chat/completions", async (HttpContext http) =>
        {
            try { return await server.Respond(http); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                server.Errors.Enqueue(error.ToString());
                return Results.Problem("Scripted model protocol assertion failed.");
            }
        });
        await app.StartAsync(ct);
        return server;
    }
    private async Task<IResult> Respond(HttpContext http)
    {
        using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var request = document.RootElement.Clone();

        var messages = request.GetProperty("messages").EnumerateArray().ToArray();
        var lastUser = messages.LastOrDefault(m => m.GetProperty("role").GetString() == "user") is { } user
            ? user.GetProperty("content").GetString() ?? ""
            : "";
        // Startup verifies the shipped prompt/group-chat packages through this same provider.
        var instructions = string.Join("\n", messages.Where(m => m.GetProperty("role").GetString() is "system" or "developer")
            .Select(m => m.GetProperty("content").GetString()));
        string? backgroundReply = null;
        if (lastUser == "What is the capital of France?" && instructions.StartsWith("You are a helpful assistant. Answer the user's question directly in at most three clear sentences.", StringComparison.Ordinal))
        { backgroundReply = "Paris is the capital of France."; }
        else if (lastUser.StartsWith("Question: Name one product idea for dog owners", StringComparison.Ordinal)
            && instructions.Contains("You are brainstorming with another model to give the user one good answer.", StringComparison.Ordinal))
        { backgroundReply = "AGREE: A smart dog bowl that logs every meal."; }
        else if (instructions == "You judge whether an answer meets a criterion. Reply with YES or NO on the first line, then one short reason."
            && lastUser == "Criterion: It proposes one concrete product for dog owners\n\nAnswer:\nAGREE: A smart dog bowl that logs every meal.")
        { backgroundReply = "YES\nThe dog bowl is a concrete product for dog owners."; }
        if (backgroundReply is not null)
        {
            Assert.False(request.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0);
            return Completion(request, new { role = "assistant", content = backgroundReply }, "stop");
        }
        // Any other request is a conversation turn: it is recorded and answered in plain words.
        _requests.Enqueue(request);
        return Completion(request, new { role = "assistant", content = "You said: " + lastUser }, "stop");
    }

    // A streaming request is answered as a stream: the delta, the finish, then the usage chunk the
    // SDK reads token counts from. A plain JSON body to a streaming client parses as no events.
    private static IResult Completion(JsonElement request, object message, string reason)
    {
        if (request.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
        {
            static object Chunk(object[] choices, object? usage) => new { id = "fixture-response", @object = "chat.completion.chunk", created = 1, model = "gpt-5.6-luna", choices, usage };
            var events = new[]
            {
                Chunk([new { index = 0, delta = message, finish_reason = (string?)null }], null),
                Chunk([new { index = 0, delta = new { }, finish_reason = (string?)reason }], null),
                Chunk([], new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }),
            };
            var body = string.Concat(events.Select(chunk => "data: " + JsonSerializer.Serialize(chunk) + "\n\n")) + "data: [DONE]\n\n";
            return Results.Text(body, "text/event-stream");
        }

        return Results.Json(new { id = "fixture-response", @object = "chat.completion", created = 1, model = "gpt-5.6-luna", choices = new[] { new { index = 0, message, finish_reason = reason } }, usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 } });
    }

    public void AssertNoProtocolErrors() => Assert.Empty(Errors);
    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
