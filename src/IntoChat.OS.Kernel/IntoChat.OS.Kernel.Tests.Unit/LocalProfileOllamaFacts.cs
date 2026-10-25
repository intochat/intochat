extern alias AppHost;
using AppHost::IntoChat.OS.AppHost;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Models.Ollama;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Platform.Integrations;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The local profile talks to the Ollama already running on the host: an endpoint and a default model, no container, no key.
[Collection(AppHostComposition.Collection)]
public sealed class LocalProfileOllamaFacts
{
    private const string MasterKey = "Parameters:Modules-intochat-os-master-key=synthetic-composition-test-key-32chars";

    [Fact]
    public void TheLeanBrainDefaultsToTheHostOllamaAndQwen35FourB()
    {
        var ai = RunProfile.LocalLean.Ai;
        Assert.Equal("http://localhost:11434", ai.Ollama.Endpoint);
        Assert.Equal(nameof(IQwen35), ai.Default.Model);
        Assert.Equal("qwen3.5:4b", RunProfile.DefaultLocalModel);
        Assert.Empty(ai.Hosting.Llms);
        Assert.Empty(ai.Hosting.Embeddings);
        Assert.Empty(ai.Hosting.SystemOnes);
        Assert.Empty(ai.Ollama.Models);
        Assert.False(ai.Tavily.Enabled);
        Assert.True(RunProfile.LocalLean.Lean);
    }

    [Fact]
    public void TheModelOverrideBecomesTheOllamaModelOfTheDefaultMarker()
        => Assert.Equal("qwen3.5:2b", RunProfile.LocalLeanWith("qwen3.5:2b").Ai.Ollama.Models[nameof(IQwen35)].Model);

    [Fact]
    public async Task TheLocalProfileStartsNoOllamaContainerAndNoKeyParameterAndProjectsTheEndpoint()
    {
        await using var host = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, "--" + IntoChat.OS.Framework.IntoChatOSNames.TopologyKey + "=" + IntoChat.OS.Framework.IntoChatOSNames.TopologyLocal], TestContext.Current.CancellationToken);
        Assert.DoesNotContain(host.Resources, resource => resource.Name.Contains("ollama", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(host.Resources, resource => resource.Name.Contains("openwebui", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(host.Resources.OfType<ParameterResource>(), resource =>
            resource.Name.Contains("api-key", StringComparison.OrdinalIgnoreCase) || resource.Name.Contains("apikey", StringComparison.OrdinalIgnoreCase));

        var kernel = Assert.Single(host.Resources.OfType<ProjectResource>(), resource => resource.Name == "OperatingSystem-a");
        var environment = await EnvironmentOf(host, kernel);
        Assert.Equal("http://localhost:11434", environment[AIKeys.OllamaEndpoint]);
        Assert.Equal(nameof(IQwen35), environment[AIKeys.DefaultModel]);
        foreach (var provider in new[] { "openai", "anthropic", "google", "xai", "openrouter", "tavily" })
        {
            Assert.DoesNotContain(IntegrationSchema.Key(provider, "ApiKey"), environment.Keys);
        }
    }

    [Fact]
    public async Task TheModelOverrideReachesTheKernelUnderTheDefaultMarkersModelKey()
    {
        await using var host = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, "--" + IntoChat.OS.Framework.IntoChatOSNames.TopologyKey + "=" + IntoChat.OS.Framework.IntoChatOSNames.TopologyLocal, "--" + IntoChatOSHostingOptions.LocalModelKey + "=qwen3.5:2b"], TestContext.Current.CancellationToken);
        var kernel = Assert.Single(host.Resources.OfType<ProjectResource>(), resource => resource.Name == "OperatingSystem-a");
        var environment = await EnvironmentOf(host, kernel);
        Assert.Equal("qwen3.5:2b", environment[AIKeys.OllamaModels + $":{nameof(IQwen35)}:Model"]);
    }

    internal static async Task<Dictionary<string, string>> EnvironmentOf(IDistributedApplicationTestingBuilder host, IResource resource)
    {
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(host.ExecutionContext, resource, environment, TestContext.Current.CancellationToken);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await callback.Callback(context); }
        return environment.ToDictionary(pair => IntoChatOSConfiguration.ConfigurationName(pair.Key), pair => pair.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }
}
