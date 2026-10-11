using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;

namespace DigitalBrain.Modules.AI.Tests.Agents;

public sealed class HostedAgentFacts
{
    // Formerly the AI E2E suite's separate-host journey; the adapter, the integration
    // configuration plumbing and the commit-before-signal ordering are host-agnostic, so the
    // in-process brain proves them against the same loopback provider endpoint.
    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OpenRouter")]
    public async Task RealProviderAdapterAnswersAndCommitsTheTranscriptBeforeSignal(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        using var endpoint = new LoopbackServer();
        var integration = provider == "OpenAI" ? AIModule.OpenAIIntegration : AIModule.OpenRouterIntegration;
        await using var brain = await TestBrain.Create().WithModule<AIModule, AIOptions>(options =>
            {
                options.Default.Model = "fixture";
                options.ModelProfiles.Add("fixture", new AIModelProfileOptions
                {
                    Provider = provider,
                    Model = "fixture-model",
                    Endpoint = endpoint.Url,
                    Capabilities = LlmCapabilities.None,
                });
            })
            .WithIntegration(integration, "ApiKey", "test-only")
            .WithIntegration(integration, "Endpoint", endpoint.Url)
            .StartAsync(ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("hosted-assistant"));
        await using var completions = await brain.Observe<TurnCompleted>(agent, ct);
        var firstRequest = ReplyWithStream(endpoint, "first answer", ct);
        var firstResponse = agent.GetResponse("first question", ct);
        Assert.Equal("first answer", (await completions.NextAsync(ct: ct)).Reply);
        Assert.Equal(2, (await agent.Transcript()).Length);
        Assert.Equal("first answer", await firstResponse);
        using (var request = JsonDocument.Parse((await firstRequest).Body))
        {
            Assert.Equal("fixture-model", request.RootElement.GetProperty("model").GetString());
        }

        var secondRequest = ReplyWithStream(endpoint, "second answer", ct);
        Assert.Equal("second answer", await agent.GetResponse("follow up", ct));
        using (var request = JsonDocument.Parse((await secondRequest).Body))
        {
            var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Contains(messages, m => m.GetProperty("role").GetString() == "assistant"
                && m.GetProperty("content").ToString().Contains("first answer", StringComparison.Ordinal));
        }
        Assert.Equal(4, (await agent.Transcript()).Length);

        var inferenceRequest = ReplyWithCompletion(endpoint, "standalone", ct);
        var result = await brain.Get<ILLM>("fixture").Generate(
            new InferenceRequest([new AiMessage("user", [new AiText("independent")])]), cancellationToken: ct);
        Assert.Equal("standalone", Assert.IsType<AiText>(Assert.Single(Assert.Single(result.Messages).Content)).Text);
        await inferenceRequest;
        Assert.Equal(4, (await agent.Transcript()).Length);
    }

    // The agent streams every turn, so the provider answers it as server-sent chunks.
    private static Task<CapturedRequest> ReplyWithStream(LoopbackServer endpoint, string text, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        string Chunk(object delta, string? finish) => "data: " + JsonSerializer.Serialize(new
        {
            id,
            @object = "chat.completion.chunk",
            created = 1,
            model = "fixture-model",
            choices = new[] { new { index = 0, delta, finish_reason = finish } },
        }) + "\n\n";
        return endpoint.ReplyOnce("text/event-stream",
            Chunk(new { role = "assistant", content = text }, null) + Chunk(new { }, "stop") + "data: [DONE]\n\n", ct);
    }

    private static Task<CapturedRequest> ReplyWithCompletion(LoopbackServer endpoint, string text, CancellationToken ct) =>
        endpoint.ReplyOnce("application/json", JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"),
            @object = "chat.completion",
            created = 1,
            model = "fixture-model",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 2, completion_tokens = 1, total_tokens = 3 },
        }), ct);
}
