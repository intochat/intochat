using System.Net;
using System.Text;
using Anthropic;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class AnthropicWebSearchProbeFacts
{
    [Fact]
    public async Task AnthropicMapsHostedWebSearchToAServerTool()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler(body => capturedBody = body);
        using var http = new HttpClient(handler);
        using var client = new AnthropicClient
        {
            ApiKey = "sk-ant-test",
            HttpClient = http,
        }.AsIChatClient("claude-sonnet-4-5");

        var options = new ChatOptions
        {
            Tools = [new HostedWebSearchTool()],
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "latest .NET release")],
            options,
            TestContext.Current.CancellationToken);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"web_search", capturedBody, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler(Action<string> onBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            onBody(body);

            const string responseJson = """
            {
              "id": "msg_probe",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-5",
              "content": [{ "type": "text", "text": "ok" }],
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 1, "output_tokens": 1 }
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
