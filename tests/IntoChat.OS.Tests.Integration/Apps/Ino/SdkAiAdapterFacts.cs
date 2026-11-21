using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Hosting;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Kernel.Platform;

namespace IntoChat.OS.Apps.Tests.Integration.Ino;

public sealed class SdkAiAdapterFacts(InoSiloFixture fixture) : IClassFixture<InoSiloFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SdkAskAnswersWhenADefaultModelIsConfigured()
    {
        var brain = fixture.NewBrain("sdk-ask-host");
        fixture.Model.Reply("42");

        var sdk = new SdkAiAdapter(fixture.Services);
        var answer = await sdk.AskAsync(brain, "What is the answer?", "Reply concisely.", Ct);

        Assert.Equal("42", answer);
    }

    [Fact]
    public async Task SdkAskAnswersWithHostDefaultEvenWhenCatalogListsNoProviderKeys()
    {
        var client = new SwappableChatModel();
        client.Reply("configured-answer");
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration([typeof(AIModule).Assembly, typeof(AIModule).Assembly == AIModule.Contracts ? typeof(AIModule).Assembly : AIModule.Contracts!]),
            services =>
            {
                AIModule.ConfigureServices(services, config);
                services.AddIntoChatOSConnections();
                services.TryAddSingleton<IReadOnlyList<IntegrationSchema>>(_ => AIModule.Integrations);
                services.TryAddSingleton<IBrainSecretSealer, TestSecretSealer>();
                services.Configure<AIOptions>(options => options.Roles.Device[Role.Chat] = "qwen3.5:4b");
                services.RemoveAll<IChatClient>();
                services.AddSingleton<IChatClient>(client);
                services.RemoveAll<IAiCredentials>();
                services.AddSingleton<IAiCredentials, NoConfiguredCredentials>();
            });

        await using var host = await factory.Start(
            new InProcessHostOptions { Modules = [typeof(AIModule)] },
            Ct);

        var sdk = new SdkAiAdapter(host.Services);
        var answer = await sdk.AskAsync("sdk-host-only", "Say hi", "Be brief", Ct);

        Assert.Equal("configured-answer", answer);
    }

    [Fact]
    public async Task SdkAskAnswersWhenDeploymentDefaultModelAndKeyExist()
    {
        var brain = "sdk-default-keyed";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Home"] = Path.Combine(Path.GetTempPath(), "intochat-sdk-ai-" + Guid.NewGuid().ToString("N")),
            })
            .Build();

        var recording = new RecordingInference();
        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration([typeof(AIModule).Assembly, typeof(AIModule).Assembly == AIModule.Contracts ? typeof(AIModule).Assembly : AIModule.Contracts!]),
            services =>
            {
                AIModule.ConfigureServices(services, config);
                services.Configure<AIOptions>(options =>
                {
                    options.Roles.Device[Role.Chat] = "qwen3.5:4b";
                });
                services.RemoveAll<IModelInference>();
                services.AddSingleton<IModelInference>(recording);
            });

        await using var host = await factory.Start(
            new InProcessHostOptions { Modules = [typeof(AIModule)] },
            Ct);

        var sdk = new SdkAiAdapter(host.Services);
        var answer = await sdk.AskAsync(brain, "Ping", "System rule", Ct);

        Assert.Equal("default-pong", answer);
        Assert.Equal(brain, recording.LastBrainId);
        Assert.Null(recording.LastSelection);
    }

    [Fact]
    public async Task SdkAskThrowsNoProviderConfiguredWhenNoModelOrProviderExists()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration([typeof(AIModule).Assembly, typeof(AIModule).Assembly == AIModule.Contracts ? typeof(AIModule).Assembly : AIModule.Contracts!]),
            services =>
            {
                AIModule.ConfigureServices(services, config);
                services.AddIntoChatOSConnections();
                services.TryAddSingleton<IReadOnlyList<IntegrationSchema>>(_ => AIModule.Integrations);
                services.TryAddSingleton<IBrainSecretSealer, TestSecretSealer>();
                services.RemoveAll<IChatClient>();
                services.RemoveAll<IAiCredentials>();
                services.AddSingleton<IAiCredentials, NoConfiguredCredentials>();
            });

        await using var host = await factory.Start(
            new InProcessHostOptions { Modules = [typeof(AIModule)] },
            Ct);

        var sdk = new SdkAiAdapter(host.Services);
        await Assert.ThrowsAsync<NoProviderConfiguredException>(
            () => sdk.AskAsync("sdk-unconfigured", "Hello", null, Ct));
    }

    private sealed class RecordingInference : IModelInference
    {
        public string? LastBrainId { get; private set; }
        public ModelSelection? LastSelection { get; private set; }

        public bool HasProfile(string name) => false;

        public Task<ModelDescriptor> Describe(ModelSelection? selection = null, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
            => Task.FromResult(new ModelDescriptor("Host", "qwen3.5:4b", null, "rev", CapabilitySupport.Supported, CapabilitySupport.Unknown, CapabilitySupport.Unknown));

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
        {
            LastBrainId = brainId;
            LastSelection = request.Model;
            return Task.FromResult(new InferenceResult([new AiMessage("assistant", [new AiText("default-pong")])], "stop", null, "r1", "qwen3.5:4b"));
        }

        public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, ModelId? model = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default, string? brainId = null)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoConfiguredCredentials : IAiCredentials
    {
        public Task<IntegrationSnapshot> StatusOf(string integrationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = integrationId, Status = IntegrationStatus.Unconfigured });
        public Task<long> GenerationOf(string integrationId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<ReleasedIntegration> MaterializeAsync(string integrationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReleaseSecretAsync(string integrationId, string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> BrainKey(string brainId, AiProvider provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class TestSecretSealer : IBrainSecretSealer
    {
        public Task<SealedSecret> Seal(string brainId, string name, string plaintext) => Seal(brainId, name, plaintext, CancellationToken.None);
        public Task<SealedSecret> Seal(string brainId, string name, string plaintext, CancellationToken cancellationToken)
        {
            _ = name;
            _ = cancellationToken;
            return Task.FromResult(new SealedSecret { KeyId = brainId, Cipher = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)) });
        }
        public Task<string> Open(string brainId, SealedSecret secret) => Open(brainId, secret, CancellationToken.None);
        public Task<string> Open(string brainId, SealedSecret secret, CancellationToken cancellationToken)
        {
            _ = brainId;
            _ = cancellationToken;
            return Task.FromResult(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(secret.Cipher)));
        }
    }
}
