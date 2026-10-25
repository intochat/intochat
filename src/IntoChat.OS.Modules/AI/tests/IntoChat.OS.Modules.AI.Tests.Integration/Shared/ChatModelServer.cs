using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// An OpenAI-compatible endpoint the real adapter talks to, answering from a script written as an
// IChatClient. One loopback server serves the test process; a brain reaches its own script through
// the API key its openai integration carries.
internal static class ChatModelServer
{
    private static readonly ConcurrentDictionary<string, IChatClient> Scripts = new(StringComparer.Ordinal);
    private static readonly Lazy<Uri> Endpoint = new(Start);

    public static TestBrain WithChatModel(this TestBrain brain, IChatClient script)
    {
        var key = "script-" + Guid.NewGuid().ToString("N");
        Scripts[key] = script;
        return brain.WithIntegration(AIModule.OpenAIIntegration, "ApiKey", key)
            .WithIntegration(AIModule.OpenAIIntegration, "Endpoint", Endpoint.Value);
    }

    private static Uri Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/v1/chat/completions", (Func<HttpContext, Task>)Respond);
        app.StartAsync().GetAwaiter().GetResult();
        return new Uri(new Uri(app.Urls.Single()), "/v1/");
    }

    private static async Task Respond(HttpContext http)
    {
        var aborted = http.RequestAborted;
        var authorization = http.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !Scripts.TryGetValue(authorization["Bearer ".Length..], out var script))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: aborted);
        var request = document.RootElement;
        var model = request.GetProperty("model").GetString() ?? "";
        var messages = Messages(request);
        var options = Options(request, model);
        if (!(request.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True))
        {
            ChatResponse response;
            try { response = await script.GetResponseAsync(messages, options, aborted); }
            catch (Exception error) when (!aborted.IsCancellationRequested) { await Fail(http, error); return; }
            var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
            await http.Response.WriteAsJsonAsync(new
            {
                id = "fake",
                @object = "chat.completion",
                created = 1,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = calls.Length == 0 ? response.Text : null, tool_calls = calls.Length == 0 ? null : calls.Select(Call).ToArray() },
                        finish_reason = calls.Length == 0 ? "stop" : "tool_calls",
                    },
                },
                usage = Usage,
            }, aborted);
            return;
        }

        await using var updates = script.GetStreamingResponseAsync(messages, options, aborted).GetAsyncEnumerator(aborted);
        bool more;
        try { more = await updates.MoveNextAsync(); }
        catch (Exception error) when (!aborted.IsCancellationRequested) { await Fail(http, error); return; }
        http.Response.ContentType = "text/event-stream";
        var toolCalls = 0;
        try
        {
            while (more)
            {
                foreach (var content in updates.Current.Contents)
                {
                    object? delta = content switch
                    {
                        TextContent text => new { role = "assistant", content = text.Text },
                        FunctionCallContent call => new { role = "assistant", tool_calls = new[] { new { index = toolCalls++, id = call.CallId, type = "function", function = new { name = call.Name, arguments = Arguments(call) } } } },
                        _ => null,
                    };
                    if (delta is not null) { await Chunk(http, model, [new { index = 0, delta, finish_reason = (string?)null }], null); }
                }
                more = await updates.MoveNextAsync();
            }
        }
        catch (Exception) when (!aborted.IsCancellationRequested)
        {
            // A provider that breaks mid-stream drops the connection; the adapter sees a broken response.
            http.Abort();
            return;
        }
        await Chunk(http, model, [new { index = 0, delta = new { }, finish_reason = toolCalls == 0 ? "stop" : "tool_calls" }], null);
        await Chunk(http, model, [], Usage);
        await http.Response.WriteAsync("data: [DONE]\n\n", aborted);
    }

    private static readonly object Usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 };

    private static async Task Chunk(HttpContext http, string model, object[] choices, object? usage)
    {
        await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { id = "fake", @object = "chat.completion.chunk", created = 1, model, choices, usage }) + "\n\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    // The script's exception message is the provider's error message.
    private static async Task Fail(HttpContext http, Exception error)
    {
        http.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await http.Response.WriteAsJsonAsync(new { error = new { message = error.Message, type = "server_error" } });
    }

    private static object Call(FunctionCallContent call)
        => new { id = call.CallId, type = "function", function = new { name = call.Name, arguments = Arguments(call) } };

    private static string Arguments(FunctionCallContent call) => JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());

    private static ChatMessage[] Messages(JsonElement request)
        => [.. request.GetProperty("messages").EnumerateArray().Select(message =>
        {
            var role = message.GetProperty("role").GetString();
            return role switch
            {
                "system" or "developer" => new ChatMessage(ChatRole.System, Text(message)),
                "user" => new ChatMessage(ChatRole.User, Text(message)),
                "tool" => new ChatMessage(ChatRole.Tool, [new FunctionResultContent(message.GetProperty("tool_call_id").GetString()!, Text(message))]),
                "assistant" => new ChatMessage(ChatRole.Assistant, Assistant(message)),
                _ => throw new NotSupportedException($"Role '{role}' is not an OpenAI chat role."),
            };
        })];

    private static List<AIContent> Assistant(JsonElement message)
    {
        List<AIContent> contents = [];
        if (Text(message) is { Length: > 0 } text) { contents.Add(new TextContent(text)); }
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            contents.AddRange(calls.EnumerateArray().Select(call => new FunctionCallContent(call.GetProperty("id").GetString()!,
                call.GetProperty("function").GetProperty("name").GetString()!,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(call.GetProperty("function").GetProperty("arguments").GetString() ?? "{}"))));
        }
        return contents;
    }

    private static string Text(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) { return ""; }
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => string.Concat(content.EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString())),
            _ => "",
        };
    }

    private static ChatOptions Options(JsonElement request, string model)
    {
        var options = new ChatOptions { ModelId = model };
        if (request.TryGetProperty("temperature", out var temperature) && temperature.ValueKind == JsonValueKind.Number) { options.Temperature = temperature.GetSingle(); }
        if (request.TryGetProperty("top_p", out var topP) && topP.ValueKind == JsonValueKind.Number) { options.TopP = topP.GetSingle(); }
        if ((request.TryGetProperty("max_completion_tokens", out var tokens) || request.TryGetProperty("max_tokens", out tokens)) && tokens.ValueKind == JsonValueKind.Number)
        { options.MaxOutputTokens = tokens.GetInt32(); }
        if (request.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            options.Tools = [.. tools.EnumerateArray().Select(tool => tool.GetProperty("function")).Select(function =>
                AIFunctionFactory.CreateDeclaration(function.GetProperty("name").GetString()!,
                    function.TryGetProperty("description", out var description) ? description.GetString() : null,
                    function.TryGetProperty("parameters", out var parameters) ? parameters.Clone() : JsonDocument.Parse("{}").RootElement.Clone()))];
        }
        return options;
    }
}
