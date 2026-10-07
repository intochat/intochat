using System.Globalization;
using System.Text;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.Assistant;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.VoiceInput;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ChatRole = DigitalBrain.Flutter.Chat.ChatRole;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class AssistantFeatureFacts
{
    private static async Task<ModuleBrain> StartBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        return await ModuleTest.Create().WithExecution(new TestExecutionOptions
        {
            PrivateConfiguration = new Dictionary<string, string?> { [IntegrationSchema.Key("openai", "ApiKey")] = "test-no-network" },
        })
            .WithModule<AssistantModule>().RequireModules([new DigitalBrain.Apps.AppsModule(), new DigitalBrain.AI.AIModule(), new DigitalBrain.Compute.ComputeModule(), new DigitalBrain.Flutter.FlutterModule()])
            .ConfigureSilo(silo => silo.Services
                .Configure<AIOptions>(options => { options.ModelProfiles["default"] = new() { Provider = "OpenAI", Model = "fixture", Capabilities = LlmCapabilities.Tools }; options.Default.Model = "default"; })
                .AddSingleton<IChatClient>(new ScriptedAssistantModel())
                .AddSingleton<IAudioTranscriptionService>(new TextTranscriber()))
            .StartAsync(ct);
    }

    [Fact]
    public async Task ConfigurationSurvivesReactivation()
    {
        await using var brain = await StartBrain();
        var app = brain.Get<IAssistant>(Key);
        await app.Configure(new AgentDefinition { DisplayName = "Custom assistant", Instructions = "Be concise." });
        await brain.DeactivateAsync(app, TestContext.Current.CancellationToken);
        Assert.Equal("Be concise.", (await app.DefineTurn(null)).Instructions);
    }

    [Fact]
    public async Task TurnsRetainSummaryAndRejectInvalidConversationIdentifiers()
    {
        await using var brain = await StartBrain();
        var app = brain.Get<IAssistant>(Key);
        Assert.Contains("Earlier conversation summary: Earlier work", (await app.DefineTurn("Earlier work")).Instructions, StringComparison.Ordinal);
        foreach (var invalid in new[] { "", "foreign/thread", "foreign\\thread", new string('x', 201) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => app.Conversation(invalid));
        }
    }

    private const string Key = "assistant-facts/applications/assistant";

    // Audio in these scenarios is the UTF-8 text the user "said".
    private sealed class TextTranscriber : IAudioTranscriptionService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public bool InitializationFailed => false;
        public string? ErrorMessage => null;
        public string ModelId => "text";
        public Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<string> TranscribeAsync(Stream audioStream, string fileName, CancellationToken cancellationToken = default)
        {
            audioStream.Position = 44; // The fixture stores its transcript after a WAV header.
            using var reader = new StreamReader(audioStream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
    }
}
