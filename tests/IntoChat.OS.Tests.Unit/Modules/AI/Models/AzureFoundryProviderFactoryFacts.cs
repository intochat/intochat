using System.ClientModel;
using System.ClientModel.Primitives;
using Aspire.Hosting;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.AzureFoundry;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class AzureFoundryProviderFactoryFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://my-foundry.services.ai.azure.com", "https://my-foundry.services.ai.azure.com/openai/v1")]
    [InlineData("https://my-foundry.services.ai.azure.com/", "https://my-foundry.services.ai.azure.com/openai/v1")]
    [InlineData("https://my-foundry.services.ai.azure.com/openai", "https://my-foundry.services.ai.azure.com/openai/v1")]
    [InlineData("https://my-foundry.services.ai.azure.com/openai/", "https://my-foundry.services.ai.azure.com/openai/v1")]
    [InlineData("https://my-foundry.services.ai.azure.com/openai/v1", "https://my-foundry.services.ai.azure.com/openai/v1")]
    [InlineData("https://my-foundry.services.ai.azure.com/openai/v1/", "https://my-foundry.services.ai.azure.com/openai/v1")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/openai/v1")]
    [InlineData("http://localhost:8080/openai/v1", "http://localhost:8080/openai/v1")]
    public void NormalizesOpenAiV1Endpoint(string raw, string expected)
    {
        var normalized = AzureFoundryProviderFactory.NormalizeEndpoint(raw);
        Assert.Equal(expected, normalized.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://my-foundry.services.ai.azure.com")]
    [InlineData("ftp://my-foundry.services.ai.azure.com")]
    [InlineData("https://user:pass@my-foundry.services.ai.azure.com")]
    [InlineData("https://my-foundry.services.ai.azure.com?api-version=1")]
    [InlineData("https://my-foundry.services.ai.azure.com#frag")]
    [InlineData("not-a-uri")]
    [InlineData("")]
    public void RejectsInvalidEndpoints(string raw)
        => Assert.Throws<ArgumentException>(() => AzureFoundryProviderFactory.NormalizeEndpoint(raw));

    [Theory]
    [InlineData("https://my-foundry.services.ai.azure.com/openai/v1", "wss://my-foundry.services.ai.azure.com/openai/v1/realtime")]
    [InlineData("https://my-foundry.services.ai.azure.com/voice-live/realtime", "wss://my-foundry.services.ai.azure.com/voice-live/realtime")]
    [InlineData("http://127.0.0.1:9000/openai/v1", "ws://127.0.0.1:9000/openai/v1/realtime")]
    public void DerivesRealtimeWebSocketEndpoint(string openAiEndpoint, string expectedWs)
    {
        var ws = AzureFoundryProviderFactory.RealtimeEndpointOf(new Uri(openAiEndpoint, UriKind.Absolute));
        Assert.Equal(expectedWs, ws.AbsoluteUri);
    }

    [Theory]
    [InlineData("DeepSeek-V3.2", "DeepSeek-V3.2")]
    [InlineData("DeepSeek-R1", "DeepSeek-R1")]
    [InlineData("foundry/DeepSeek-V3.2", "DeepSeek-V3.2")]
    [InlineData("foundry/gpt-realtime", "gpt-realtime")]
    [InlineData("foundry/gpt-4o-transcribe", "gpt-4o-transcribe")]
    [InlineData("foundry/gpt-4o-mini-tts", "gpt-4o-mini-tts")]
    [InlineData("foundry/text-embedding-3-small", "text-embedding-3-small")]
    [InlineData("foundry/gpt-image-1", "gpt-image-1")]
    public void StripsFoundryPrefixForWireDeploymentName(string modelId, string expectedDeployment)
        => Assert.Equal(expectedDeployment, AzureFoundryProviderFactory.DeploymentNameOf(modelId));

    [Fact]
    public async Task KeylessEntraIdIsDefaultWhenEndpointIsConfigured()
    {
        var tokenProviderCalls = 0;
        var factory = new AzureFoundryProviderFactory(() =>
        {
            tokenProviderCalls++;
            return new StubTokenProvider();
        });
        var options = new AIOptions
        {
            AzureFoundry = new AzureFoundryOptions
            {
                Endpoint = "https://intochat-dev.services.ai.azure.com",
            },
        };
        var credentials = new FixedAiCredentials();

        Assert.True(options.AzureFoundry.UseEntraId);
        Assert.True(await factory.IsConfigured(options, credentials, Ct));

        var (endpoint, apiKey, policy) = await factory.ResolveAuthAsync(options, credentials, pinnedEndpoint: null, brainId: null, Ct);

        Assert.Equal("https://intochat-dev.services.ai.azure.com/openai/v1", endpoint.AbsoluteUri);
        Assert.Null(apiKey);
        Assert.IsType<BearerTokenPolicy>(policy);
        Assert.Equal(1, tokenProviderCalls);
    }

    [Fact]
    public async Task ExplicitApiKeyUsesApiKeyCredentialWithoutEntraId()
    {
        var tokenProviderCalls = 0;
        var factory = new AzureFoundryProviderFactory(() =>
        {
            tokenProviderCalls++;
            return new StubTokenProvider();
        });
        var options = new AIOptions
        {
            AzureFoundry = new AzureFoundryOptions
            {
                Endpoint = "https://intochat-dev.services.ai.azure.com",
            },
        };
        var credentials = new FixedAiCredentials()
            .Ready(AiIntegrations.IdOf(AiProvider.AzureFoundry), "foundry-test-value");

        Assert.True(await factory.IsConfigured(options, credentials, Ct));

        var (endpoint, apiKey, policy) = await factory.ResolveAuthAsync(options, credentials, pinnedEndpoint: null, brainId: null, Ct);

        Assert.Equal("https://intochat-dev.services.ai.azure.com/openai/v1", endpoint.AbsoluteUri);
        Assert.Equal("foundry-test-value", apiKey);
        Assert.Null(policy);
        Assert.Equal(0, tokenProviderCalls);
    }

    [Fact]
    public async Task UnconfiguredWhenEntraIdDisabledAndNoApiKey()
    {
        var factory = new AzureFoundryProviderFactory(static () => new StubTokenProvider());
        var options = new AIOptions
        {
            AzureFoundry = new AzureFoundryOptions
            {
                Endpoint = "https://intochat-dev.services.ai.azure.com",
                UseEntraId = false,
            },
        };
        var credentials = new FixedAiCredentials();

        Assert.False(await factory.IsConfigured(options, credentials, Ct));
        await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => factory.ResolveAuthAsync(options, credentials, pinnedEndpoint: null, brainId: null, Ct));
    }

    [Theory]
    [InlineData("https://my-foundry.services.ai.azure.com")]
    [InlineData("https://my-openai.openai.azure.com")]
    [InlineData("https://my-cog.cognitiveservices.azure.com")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    public async Task EntraIdAllowsTrustedAzureAndLoopbackHosts(string endpointUrl)
    {
        var factory = new AzureFoundryProviderFactory(static () => new StubTokenProvider());
        var options = new AIOptions
        {
            AzureFoundry = new AzureFoundryOptions
            {
                Endpoint = endpointUrl,
                UseEntraId = true,
            },
        };
        var credentials = new FixedAiCredentials();

        Assert.True(await factory.IsConfigured(options, credentials, Ct));
        var (_, apiKey, policy) = await factory.ResolveAuthAsync(options, credentials, pinnedEndpoint: null, brainId: null, Ct);
        Assert.Null(apiKey);
        Assert.IsType<BearerTokenPolicy>(policy);
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("https://evilservices.ai.azure.com")]
    [InlineData("https://services.ai.azure.com")]
    [InlineData("https://my-foundry.services.ai.azure.com.evil.com")]
    public async Task EntraIdRejectsNonAzureNonLoopbackHosts(string untrustedEndpoint)
    {
        var factory = new AzureFoundryProviderFactory(static () => new StubTokenProvider());
        var options = new AIOptions
        {
            AzureFoundry = new AzureFoundryOptions
            {
                Endpoint = untrustedEndpoint,
                UseEntraId = true,
            },
        };
        var credentials = new FixedAiCredentials();

        Assert.False(await factory.IsConfigured(options, credentials, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => factory.ResolveAuthAsync(options, credentials, pinnedEndpoint: null, brainId: null, Ct));
    }

    [Fact]
    public void CatalogIncludesAllSevenAzureFoundryCards()
    {
        Assert.Equal((AiProvider.AzureFoundry, Where.Cloud, Abilities.Text | Abilities.Tools),
            (KnownModels.FoundryDeepSeekV32.Provider, KnownModels.FoundryDeepSeekV32.Where, KnownModels.FoundryDeepSeekV32.Can));
        Assert.Equal((AiProvider.AzureFoundry, Where.Cloud, Abilities.Text | Abilities.Tools | Abilities.Reasoning),
            (KnownModels.FoundryDeepSeekR1.Provider, KnownModels.FoundryDeepSeekR1.Where, KnownModels.FoundryDeepSeekR1.Can));
        Assert.Equal((AiProvider.AzureFoundry, Where.Cloud, Abilities.Hear),
            (KnownModels.FoundryGpt4oTranscribe.Provider, KnownModels.FoundryGpt4oTranscribe.Where, KnownModels.FoundryGpt4oTranscribe.Can));
        Assert.Equal((AiProvider.AzureFoundry, Where.Cloud, Abilities.Speak),
            (KnownModels.FoundryGpt4oMiniTts.Provider, KnownModels.FoundryGpt4oMiniTts.Where, KnownModels.FoundryGpt4oMiniTts.Can));
        Assert.Equal((AiProvider.AzureFoundry, Where.Cloud, Abilities.Talk),
            (KnownModels.FoundryGptRealtime.Provider, KnownModels.FoundryGptRealtime.Where, KnownModels.FoundryGptRealtime.Can));
        Assert.Equal((AiProvider.AzureFoundry, Where.Cloud, Abilities.Embed),
            (KnownModels.FoundryTextEmbedding3Small.Provider, KnownModels.FoundryTextEmbedding3Small.Where, KnownModels.FoundryTextEmbedding3Small.Can));
        Assert.Equal((AiProvider.AzureFoundry, Where.Cloud, Abilities.Image),
            (KnownModels.FoundryGptImage1.Provider, KnownModels.FoundryGptImage1.Where, KnownModels.FoundryGptImage1.Can));
    }

    [Fact]
    public void CloudOnlyFoundryDeploymentResolvesUnderLocalTopologyWithoutDevice()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                [AIKeys.AzureFoundryEndpoint] = "https://intochat-dev.services.ai.azure.com",
            })
            .Build();

        var deployment = new AIDeployment()
            .Cloud(c => c.AzureFoundry()
                .Roles(r => r
                    .Chat(KnownModels.FoundryDeepSeekV32)
                    .Fast(KnownModels.FoundryDeepSeekV32)
                    .Think(KnownModels.FoundryDeepSeekR1)
                    .Hear(KnownModels.FoundryGpt4oTranscribe)
                    .Speak(KnownModels.FoundryGpt4oMiniTts)
                    .Talk(KnownModels.FoundryGptRealtime)
                    .Embed(KnownModels.FoundryTextEmbedding3Small)
                    .Image(KnownModels.FoundryGptImage1)));

        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [$"--{IntoChatOSNames.TopologyKey}={IntoChatOSNames.TopologyLocal}", "--IntoChatOS:Shell:Enabled=false"],
            DisableDashboard = true,
        });
        var profile = DeploymentProfile.From(appBuilder);
        var plan = deployment.Resolve(profile, config);

        Assert.Null(plan.Devices);
        Assert.Equal("https://intochat-dev.services.ai.azure.com", plan.Ai.AzureFoundry.Endpoint);
        Assert.True(plan.Ai.AzureFoundry.UseEntraId);
        Assert.Empty(plan.Ai.Roles.Device);
        Assert.Equal("DeepSeek-V3.2", plan.Ai.Roles.Cloud[ModelRole.Chat]);
        Assert.Equal("DeepSeek-V3.2", plan.Ai.Roles.Cloud[ModelRole.Fast]);
        Assert.Equal("DeepSeek-R1", plan.Ai.Roles.Cloud[ModelRole.Think]);
        Assert.Equal("foundry/gpt-4o-transcribe", plan.Ai.Roles.Cloud[ModelRole.Hear]);
        Assert.Equal("foundry/gpt-4o-mini-tts", plan.Ai.Roles.Cloud[ModelRole.Speak]);
        Assert.Equal("foundry/gpt-realtime", plan.Ai.Roles.Cloud[ModelRole.Talk]);
        Assert.Equal("foundry/text-embedding-3-small", plan.Ai.Roles.Cloud[ModelRole.Embed]);
        Assert.Equal("foundry/gpt-image-1", plan.Ai.Roles.Cloud[ModelRole.Image]);
    }

    private sealed class StubTokenProvider : AuthenticationTokenProvider
    {
        public override GetTokenOptions? CreateTokenOptions(IReadOnlyDictionary<string, object> properties)
            => new(properties);

        public override AuthenticationToken GetToken(GetTokenOptions options, CancellationToken cancellationToken)
            => new("stub-bearer-token", "Bearer", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AuthenticationToken> GetTokenAsync(GetTokenOptions options, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(options, cancellationToken));
    }
}
