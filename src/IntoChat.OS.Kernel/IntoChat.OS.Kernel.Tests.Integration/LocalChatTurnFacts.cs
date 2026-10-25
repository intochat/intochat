using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Tests.Integration;

// The local profile's usable-chat proof, headless: with the AI module pointed at an Ollama on loopback (the profile's
// host Ollama, here a scripted endpoint) and the default model the profile picks (IQwen35 = qwen3.5:4b), one chat turn
// through the neuron surface that MCP's neurons_call exposes comes back as the assistant's answer. No real model runs.
public sealed class LocalChatTurnFacts
{
    private const string Answer = "Hello from the scripted local model.";

    [Trait("host", "own")]
    [Fact]
    public async Task AChatTurnThroughTheNeuronSurfaceIsAnsweredByTheLocalOllama()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ollama = await ScriptedOllama.StartAsync(Answer, ct);
        await using var brain = await TestBrain.Create()
            .WithModule<AIModule, AIOptions>(options =>
            {
                options.Ollama.Endpoint = ollama.Endpoint;
                options.Default.Model = nameof(IntoChat.OS.Modules.AI.Models.Ollama.IQwen35);
            })
            .WithConfiguration(AuthOptions.PostureKey, nameof(IdentityPosture.Open))
            .StartAsync(ct);

        // neurons_call { contract: ILLM, key: "{brain}/models", method: "Generate", arguments: "[<InferenceRequest>]" }
        // (06-05: the model neurons are plain; a plain key names its brain, so the bare "default" is no longer a model key)
        var request = new InferenceRequest([new AiMessage("user", [new AiText("Say hello.")])]);
        var arguments = JsonSerializer.Serialize(new object[] { request }, JsonSerializerOptions.Web);
        using var caller = brain.Owner.AsCaller();
        var surface = brain.Services.GetRequiredService<NeuronSurface>();
        var result = await surface.Call(typeof(ILLM).FullName!, IntoChat.OS.Modules.AI.Models.ModelAddress.Key(brain.Owner.BrainId), nameof(ILLM.Generate), arguments, ct);

        Assert.NotNull(result);
        var reply = result.Value.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("assistant", reply.GetProperty("role").GetString());
        Assert.Contains(Answer, reply.GetRawText());
        var sent = Assert.Single(ollama.Requests);
        Assert.Equal("qwen3.5:4b", sent.GetProperty("model").GetString());
        Assert.Contains("Say hello.", sent.GetProperty("messages").GetRawText());
    }

    // The part of Ollama a chat turn uses: POST /api/chat answered with one NDJSON line.
    private sealed class ScriptedOllama : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<JsonElement> _requests = new();

        private ScriptedOllama(WebApplication app) { _app = app; }

        public string Endpoint => _app.Urls.Single().TrimEnd('/');

        public IReadOnlyList<JsonElement> Requests => [.. _requests];

        public static async Task<ScriptedOllama> StartAsync(string answer, CancellationToken ct)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new ScriptedOllama(app);
            app.MapPost("/api/chat", async (HttpContext http) =>
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                server._requests.Enqueue(document.RootElement.Clone());
                var line = JsonSerializer.Serialize(new
                {
                    model = "qwen3.5:4b",
                    created_at = "2026-10-19T00:00:00Z",
                    message = new { role = "assistant", content = answer },
                    done = true,
                    done_reason = "stop",
                    prompt_eval_count = 3,
                    eval_count = 7,
                });
                return Results.Text(line + "\n", "application/x-ndjson", Encoding.UTF8);
            });
            await app.StartAsync(ct);
            return server;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
