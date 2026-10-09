using System.Text.Json;

namespace DigitalBrain.Revisions;

// One stateless tools/call per request, the way McpIdentityFacts drives the real server.
internal sealed class McpNeuronClient(HttpClient http, string mcpEndpoint, string brainId)
{
    private int _id;

    public async Task<JsonElement> Call(string tool, object arguments, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{mcpEndpoint.TrimEnd('/')}/brains/{brainId}/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = ++_id, method = "tools/call", @params = new { name = tool, arguments } }),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) { throw new InvalidOperationException($"{tool} answered {(int)response.StatusCode}: {body}"); }
        var data = body.Split('\n').FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal))?[6..] ?? body;
        using var envelope = JsonDocument.Parse(data);
        if (envelope.RootElement.TryGetProperty("error", out var error)) { throw new InvalidOperationException($"{tool}: {error}"); }
        var result = envelope.RootElement.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean()) { throw new InvalidOperationException($"{tool}: {text}"); }
        return JsonDocument.Parse(text.Length == 0 ? "null" : text).RootElement.Clone();
    }
}