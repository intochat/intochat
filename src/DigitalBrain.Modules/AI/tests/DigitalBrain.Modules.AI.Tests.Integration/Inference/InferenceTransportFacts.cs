using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Models.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Surface;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.AI.Tests.Integration;

public sealed class InferenceTransportFacts
{
    [Trait("host", "own")]
    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OpenRouter")]
    [InlineData("Ollama")]
    public async Task ToolResultsReachTheRealModelAdapterAsJson(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        using var endpoint = new LoopbackServer();
        var builder = TestBrain.Create().WithModule<AIModule, AIOptions>(options => { Configure(options, provider); options.Ollama.Endpoint = endpoint.Url; })
            // The turn composes IApp without the Apps module: its contract is put in the host's compiled layer directly.
            .ConfigureServices(services => services.AddSingleton(new DigitalBrain.Framework.CompiledTypes([typeof(INeuron).Assembly, typeof(DigitalBrain.Apps.IApp).Assembly])));
        await using var brain = await (provider == "Ollama" ? builder : builder.WithProvider(provider, endpoint.Url)).StartAsync(ct);
        async Task<CapturedRequest> Serve()
        {
            await endpoint.ReplyOnce("application/json", provider == "Ollama" ? """
                {"model":"test-model","created_at":"2026-10-03T00:00:00Z","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"neurons_context","arguments":{}}}]},"done":true,"done_reason":"stop"}
                """ : """
                {"id":"one","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"find-1","type":"function","function":{"name":"neurons_context","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                """, ct);
            return await endpoint.ReplyOnce("application/json", provider == "Ollama" ? """
                {"model":"test-model","created_at":"2026-10-03T00:00:00Z","message":{"role":"assistant","content":"done"},"done":true,"done_reason":"stop"}
                """ : """
                {"id":"two","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}
                """, ct);
        }
        var serve = Serve();
        using var caller = brain.Owner.AsCaller();
        var events = await TurnLoops.Collect(brain.Services, "find contracts", [typeof(DigitalBrain.Apps.IApp).FullName!], ct);
        Assert.Empty(events.OfType<AgentTurnEvent.Failed>());
        using var request = JsonDocument.Parse((await serve).Body);
        var result = request.RootElement.GetProperty("messages").EnumerateArray().Single(message => message.GetProperty("role").GetString() == "tool");
        if (provider == "Ollama")
        { Assert.False(request.RootElement.GetProperty("options").TryGetProperty("num_ctx", out _)); }
        using var payload = JsonDocument.Parse(result.GetProperty("content").GetString()!);
        var root = provider == "Ollama" ? payload.RootElement.GetProperty("Result") : payload.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Contains(root.EnumerateObject(), property => property.Name.Equals("BrainId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OllamaReasoningAndContextSettingsReachTheProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        using var endpoint = new LoopbackServer();
        using var services = new ServiceCollection().AddOptions().Configure<AIOptions>(options =>
        {
            options.Ollama.Endpoint = endpoint.Url;
            options.Default.Model = "local";
            options.ModelProfiles["local"] = new()
            {
                Provider = "Ollama",
                Model = "fixture",
                Endpoint = endpoint.Url,
                AllowedReasoning = ["high"],
                ContextWindowTokens = 8192
            };
        }).AddSingleton<IAiCredentials>(new FixedAiCredentials())
            .AddSingleton<ModelCatalog>().AddSingleton<InferenceService>().BuildServiceProvider();
        var serve = endpoint.ReplyOnce("application/x-ndjson", """
            {"model":"fixture","created_at":"2026-09-22T00:00:00Z","message":{"role":"assistant","content":"done"},"done":true,"done_reason":"stop","prompt_eval_count":1,"eval_count":1}
            """ + "\n", ct);
        await services.GetRequiredService<InferenceService>().Generate(new([new("user", [new AiText("hi")])],
            Options: new(Reasoning: "high", Provider: new DigitalBrain.AI.Models.Ollama.OllamaInferenceOptions(ContextWindowTokens: 6144))), cancellationToken: ct);
        using var request = JsonDocument.Parse((await serve).Body);
        Assert.Equal("high", request.RootElement.GetProperty("think").GetString());
        Assert.Equal(6144, request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RealOpenAIAdapterReturnsToolCallsWithoutExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        using var endpoint = new LoopbackServer();
        await using var brain = await TestBrain.Create().WithProvider("OpenAI", endpoint.Url).WithModule<AIModule>()
            .WithOptions<AIModule, AIOptions>(options => Configure(options, "OpenAI"))
            .StartAsync(ct);
        var serve = endpoint.ReplyOnce("application/json", """
            {"id":"response-1","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"id\":42}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
            """, ct);
        var llm = brain.Get<ILLM>("default");
        await using var started = await brain.Observe<InferenceStarted>(llm, ct);
        await using var completed = await brain.Observe<InferenceCompleted>(llm, ct);
        var result = await llm.Generate(new(
            [new("user", [new AiText("lookup")])],
            Options: new(MaxOutputTokens: 123),
            Tools: [new("lookup", "Lookup item", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}}}")]), cancellationToken: ct);
        var request = JsonDocument.Parse((await serve).Body);
        Assert.Equal("test-model", request.RootElement.GetProperty("model").GetString());
        Assert.True(request.RootElement.TryGetProperty("max_completion_tokens", out var tokens)
            || request.RootElement.TryGetProperty("max_tokens", out tokens));
        Assert.Equal(123, tokens.GetInt32());
        Assert.Single(request.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("call-1", Assert.IsType<AiToolCall>(Assert.Single(Assert.Single(result.Messages).Content)).CallId);
        Assert.Equal(15, result.Usage?.TotalTokens);
        Assert.Equal((await started.NextAsync(ct: ct)).OperationId, (await completed.NextAsync(ct: ct)).OperationId);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("OpenRouter")]
    public async Task RealStreamingAdapterPreservesTextFinishAndUsage(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        using var endpoint = new LoopbackServer();
        using var services = Services(endpoint.Url, provider);
        var serve = endpoint.ReplyOnce("text/event-stream", """
            data: {"id":"response-2","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}

            data: {"id":"response-2","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

            data: [DONE]


            """, ct);
        var updates = new List<InferenceUpdate>();
        await foreach (var update in services.GetRequiredService<InferenceService>().GenerateStreaming(
            new([new("user", [new AiText("hello")])]), cancellationToken: ct)) { updates.Add(update); }
        await serve;
        Assert.Equal("hello", string.Concat(updates.SelectMany(u => u.Content).OfType<AiText>().Select(t => t.Text)));
        Assert.Contains(updates, u => u.FinishReason == "stop");
        Assert.Contains(updates, u => u.Usage?.TotalTokens == 3);
    }

    [Fact]
    public async Task OpenRouterStreamingToolCallsPreserveIdNameAndArguments()
    {
        var ct = TestContext.Current.CancellationToken;
        using var endpoint = new LoopbackServer();
        using var services = Services(endpoint.Url, "OpenRouter");
        var serve = endpoint.ReplyOnce("text/event-stream", """
            data: {"id":"r1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"id\":"}}]},"finish_reason":null}]}

            data: {"id":"r1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"42}"}}]},"finish_reason":null}]}

            data: {"id":"r1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]


            """, ct);
        var updates = new List<InferenceUpdate>();
        await foreach (var update in services.GetRequiredService<InferenceService>().GenerateStreaming(
            new([new("user", [new AiText("lookup")])],
                Tools: [new("lookup", "Lookup", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}}}")]),
            cancellationToken: ct)) { updates.Add(update); }
        await serve;
        var call = Assert.Single(updates.SelectMany(update => update.Content).OfType<AiToolCall>());
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("lookup", call.Name);
        Assert.Contains(updates, update => update.FinishReason == "tool_calls");
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal(42, arguments.RootElement.GetProperty("id").GetInt32());
    }

    private static ServiceProvider Services(string endpoint, string provider)
        => new ServiceCollection().AddOptions().Configure<AIOptions>(options => Configure(options, provider))
            .AddSingleton<IAiCredentials>(new FixedAiCredentials().Ready(provider.ToLowerInvariant(), "test-only", endpoint))
            .AddSingleton<ModelCatalog>().AddSingleton<InferenceService>().BuildServiceProvider();

    private static void Configure(AIOptions options, string provider)
    {
        options.ModelProfiles["default"] = new() { Provider = provider, Model = "test-model", Capabilities = LlmCapabilities.Tools };
        options.Default.Model = "default";
    }
}
