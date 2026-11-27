#pragma warning disable MEAI001 // Unit tests for MEAI speech and realtime voice client construction.
using System.ClientModel;
using System.ClientModel.Primitives;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.AzureFoundry;
using IntoChat.OS.Modules.AI.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI.Audio;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class ProviderClientBuilderFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task VoiceClientsShareUnifiedFoundryAndOpenAiAndGoogleBuilders()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AIKeys.AzureFoundryEndpoint] = "https://intochat-dev.services.ai.azure.com",
            })
            .Build();

        var credentials = new FixedAiCredentials()
            .Ready(AiIntegrations.IdOf(AiProvider.OpenAI), "sk-openai")
            .Ready(AiIntegrations.IdOf(AiProvider.Google), "AIza-google");

        var builder = new ProviderClientBuilder(
            credentials,
            config,
            new LocalRuntimeInventory([]),
            foundryFactory: new AzureFoundryProviderFactory(static () => new StubTokenProvider()));

        var openAiHear = await builder.CreateSpeechToTextClientAsync("local", KnownModels.Gpt4oTranscribe, Ct);
        Assert.Null(openAiHear.Unavailable);
        Assert.NotNull(openAiHear.Client!.GetService<AudioClient>());

        var googleSpeak = await builder.CreateTextToSpeechClientAsync("local", KnownModels.Gemini38FlashTts, Ct);
        Assert.Null(googleSpeak.Unavailable);
        Assert.IsType<GeminiTextToSpeechClient>(googleSpeak.Client!.GetService<GeminiTextToSpeechClient>());

        var foundryTalk = await builder.CreateRealtimeClientAsync("local", KnownModels.FoundryGptRealtime, Ct);
        Assert.Null(foundryTalk.Unavailable);
        Assert.IsType<OpenAIRealtimeClient>(foundryTalk.Client!.GetService<OpenAIRealtimeClient>());
    }

    [Fact]
    public async Task CreateChatClientRejectsTamperedRevisionAndWrapsHostClientWithoutDisposingIt()
    {
        var hostClient = new TrackingChatClient();
        var services = new ServiceCollection();
        services.AddOptions<AIOptions>();
        services.AddSingleton<IAiCredentials>(new FixedAiCredentials());
        services.AddSingleton<IChatClient>(hostClient);

        await using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptionsMonitor<AIOptions>>();
        var inventory = new LocalRuntimeInventory(sp);
        var builder = new ProviderClientBuilder(sp, options, inventory);

        var hostModel = new ResolvedModel(AiProvider.Host, "default", null, null, string.Empty, Abilities.Text | Abilities.Tools, null, null);
        hostModel = hostModel with { Revision = ModelDecisionPolicy.ComputeRevision(hostModel) };

        using (var client = await builder.CreateChatClientAsync(null, hostModel, Ct))
        {
            Assert.NotNull(client);
        }

        Assert.False(hostClient.Disposed);

        var tampered = hostModel with { Model = "tampered" };
        await Assert.ThrowsAsync<ArgumentException>(() => builder.CreateChatClientAsync(null, tampered, Ct));
    }

    private sealed class TrackingChatClient : IChatClient
    {
        public bool Disposed { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }

    private sealed class StubTokenProvider : AuthenticationTokenProvider
    {
        public override GetTokenOptions? CreateTokenOptions(IReadOnlyDictionary<string, object> properties) => new(properties);
        public override AuthenticationToken GetToken(GetTokenOptions options, CancellationToken cancellationToken)
            => new("stub-bearer-token", "Bearer", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AuthenticationToken> GetTokenAsync(GetTokenOptions options, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(options, cancellationToken));
    }
}
