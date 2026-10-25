using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Aspire.Hosting;
using IntoChat.OS.AI.Models.FoundryLocal;
using IntoChat.OS.AI.Models.Ollama;
using IntoChat.OS.AI.Models.OpenRouter;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using static IntoChat.OS.Aspire.Testing.AspireCompositionTest;

namespace IntoChat.OS.Modules.AI.Tests.Hosting;

public sealed class CloudModelSecretFacts
{
    [Fact]
    public async Task ACloudModelProjectsNoApiKeyParameterAndForwardsAConfiguredKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IntegrationSchema.Key("openrouter", "ApiKey")] = "sk-test",
            [IntegrationSchema.Key("openai", "ApiKey")] = "  ",
        });
        var brain = builder.AddIntoChatOS("brain", new())
            .WithHosting<AIModuleHosting, AIOptions>(ai => ai.WithDefaultLlm<IDeepSeekV41Flash>().WithTavilySearch().WithoutLocalModels());
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        Assert.DoesNotContain(builder.Resources.OfType<OllamaModelResource>(), _ => true);
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), resource =>
            resource.Name.Contains("api-key", StringComparison.OrdinalIgnoreCase));
        var environment = await ConfigurationOf(builder, consumer.Resource, ct);
        Assert.Equal("sk-test", Assert.IsType<string>(environment[IntegrationSchema.Key("openrouter", "ApiKey")]));
        Assert.DoesNotContain(IntegrationSchema.Key("openai", "ApiKey"), environment.Keys);
        Assert.Contains(IntoChatOSConfiguration.ConnectionsPublicOriginKey, environment.Keys);
    }

    [Fact]
    public void ALocalModelStartsOllamaAndCreatesNoApiKeyParameter()
    {
        var builder = CreateBuilder();
        builder.AddIntoChatOS("brain", new())
            .WithHosting<AIModuleHosting, AIOptions>(ai => ai
                .WithDefaultLlm<IQwen35>()
                .WithDefaultEmbedding<IEmbeddingGemma>()
                .WithVoiceToText<IWhisperSmall>()
                .WithDefaultSystemOne<ILocalJev>()
                .WithoutWebSearch());

        Assert.Contains(builder.Resources.OfType<OllamaModelResource>(), _ => true);
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), resource =>
            resource.Name.Contains("api-key", StringComparison.OrdinalIgnoreCase));
    }
}
