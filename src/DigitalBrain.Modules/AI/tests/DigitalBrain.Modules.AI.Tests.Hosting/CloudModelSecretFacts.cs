using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.AI;
using DigitalBrain.AI.Aspire.Hosting;
using DigitalBrain.AI.Models.FoundryLocal;
using DigitalBrain.AI.Models.Ollama;
using DigitalBrain.AI.Models.OpenRouter;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using static DigitalBrain.Aspire.Testing.AspireCompositionTest;

namespace DigitalBrain.Modules.AI.Tests.Hosting;

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
        var brain = builder.AddDigitalBrain("brain", new() { PersistentStorage = false })
            .WithHosting<AIModuleHosting, AIOptions>(ai => ai.WithDefaultLlm<IDeepSeekV41Flash>().WithTavilySearch().WithoutLocalModels());
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        Assert.DoesNotContain(builder.Resources.OfType<OllamaModelResource>(), _ => true);
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), resource =>
            resource.Name.Contains("api-key", StringComparison.OrdinalIgnoreCase));
        var environment = await ConfigurationOf(builder, consumer.Resource, ct);
        Assert.Equal("sk-test", Assert.IsType<string>(environment[IntegrationSchema.Key("openrouter", "ApiKey")]));
        Assert.DoesNotContain(IntegrationSchema.Key("openai", "ApiKey"), environment.Keys);
        Assert.Contains(DigitalBrainConfiguration.ConnectionsPublicOriginKey, environment.Keys);
    }

    [Fact]
    public void ALocalModelStartsOllamaAndCreatesNoApiKeyParameter()
    {
        var builder = CreateBuilder();
        builder.AddDigitalBrain("brain", new() { PersistentStorage = false })
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
