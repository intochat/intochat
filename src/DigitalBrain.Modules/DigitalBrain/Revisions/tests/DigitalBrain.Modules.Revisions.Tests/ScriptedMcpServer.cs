using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DigitalBrain.Modules.Revisions.Tests;

// The child's MCP as a script: each tools/call is answered from the table by tool name plus
// contract/key/method, in the SSE shape the real server uses.
internal sealed class ScriptedMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];
    public string Endpoint => _app.Urls.Single();

    private ScriptedMcpServer(WebApplication app) => _app = app;

    public static async Task<ScriptedMcpServer> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var server = new ScriptedMcpServer(app);
        app.MapPost("/brains/{brain}/mcp", async (string brain, HttpContext context) =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var parameters = request.RootElement.GetProperty("params");
            var tool = parameters.GetProperty("name").GetString()!;
            var arguments = parameters.GetProperty("arguments");
            var key = tool switch
            {
                "neurons_context" => "neurons_context",
                "neurons_get" => $"neurons_get {arguments.GetProperty("contract").GetString()} {arguments.GetProperty("key").GetString()}",
                _ => $"neurons_call {arguments.GetProperty("contract").GetString()} {arguments.GetProperty("key").GetString()} {arguments.GetProperty("method").GetString()}",
            };
            server.Calls.Add(key);
            var text = server.Answers.TryGetValue(key, out var answer) ? answer : (tool == "neurons_context" ? $"{{\"principalId\":\"owner\",\"brainId\":\"{brain}\"}}" : null);
            var result = text is null
                ? new { content = new[] { new { type = "text", text = $"no script for {key}" } }, isError = true }
                : new { content = new[] { new { type = "text", text } }, isError = false };
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: message\ndata: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.RootElement.GetProperty("id").GetInt32(), result }) + "\n\n", context.RequestAborted);
        });
        await app.StartAsync(ct);
        return server;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}