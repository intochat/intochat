extern alias AppHost;
using AppHost::IntoChat.OS.AppHost;
using Aspire.Hosting.Testing;
using IntoChat.OS.Modules.AI;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.LocalSetup;
using System.Text.Json;

namespace IntoChat.OS.Kernel.Tests.Unit;

[Collection(AppHostComposition.Collection)]
public sealed class RunProfileFacts
{
    [Fact]
    public void LocalAndCloudAllocateDistinctOptionGraphs()
    {
        var first = RunProfile.Local;
        var second = RunProfile.Local;
        Assert.NotSame(first.Ai, second.Ai);
        first.Ai.Default.Model = "mutated";
        Assert.Equal("IQwen35", RunProfile.Local.Ai.Default.Model);
        Assert.Equal("IDeepSeekV41Flash", RunProfile.Cloud.Ai.Default.Model);
        Assert.Null(RunProfile.Cloud.Ai.Default.SystemOne);
        Assert.Equal("ILocalJev", RunProfile.Local.Ai.Default.SystemOne);
        Assert.False(RunProfile.Local.Ai.Tavily.Enabled);
        Assert.True(RunProfile.Cloud.Ai.Tavily.Enabled);
        Assert.Equal("http://127.0.0.1:1/v1/", RunProfile.Reference.Ai.OpenAI.Endpoint);
        Assert.False(RunProfile.Reference.Ai.Tavily.Enabled);
        Assert.Equal("IGpt56Luna", RunProfile.Reference.Ai.Default.Model);
    }

    [Theory]
    [InlineData(null, "local")]
    [InlineData("", "local")]
    [InlineData("local", "local")]
    [InlineData("LOCAL", "local")]
    [InlineData("local+cloud", "local+cloud")]
    [InlineData("local-cloud", "local+cloud")]
    [InlineData("cloud", "cloud")]
    public void TheTopologySelectsTheLeanBrainOrTheCloudBrain(string? topology, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [IntoChatOSNames.TopologyKey] = topology }).Build();
        var resolved = RunProfile.Topology(configuration);
        Assert.Equal(expected, resolved);
        var selected = RunProfile.ForTopology(resolved);
        var lean = expected != IntoChatOSNames.TopologyCloud;
        Assert.Equal(lean, selected.Lean);
        Assert.Equal(lean ? "IQwen35" : "IDeepSeekV41Flash", selected.Ai.Default.Model);
        Assert.False(selected.Orleans.Azurite.Enabled);
    }

    [Fact]
    public void AnUnknownTopologyStopsTheRunWithoutEchoingTheValue()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [IntoChatOSNames.TopologyKey] = "secret-ish-value" }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => RunProfile.Topology(configuration));
        Assert.Contains(IntoChatOSNames.TopologyKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-ish-value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsetTopologyStartsNoAzurite()
    {
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey], TestContext.Current.CancellationToken);
        Assert.DoesNotContain(appHost.Resources, IsAzurite);
    }

    [Fact]
    public async Task TheCloudTopologyStartsAzurite()
    {
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, "--" + IntoChatOSNames.TopologyKey + "=" + IntoChatOSNames.TopologyCloud], TestContext.Current.CancellationToken);
        Assert.Contains(appHost.Resources, IsAzurite);
    }

    [Fact]
    public async Task AConfigurationFileSelectsTheCloudTopology()
    {
        using var home = TestHome.Create();
        var configFile = Path.Combine(home.Path, "settings.json");
        await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(new Dictionary<string, object> { [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyCloud }), TestContext.Current.CancellationToken);
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, LocalSetupKeys.Argument(LocalSetupKeys.ConfigFile, configFile), "--" + IntoChatOSNames.HomeKey + "=" + home.Path], TestContext.Current.CancellationToken);

        Assert.Contains(appHost.Resources, IsAzurite);
    }

    [Fact]
    public async Task AConfigurationFileSelectsTheLocalModel()
    {
        using var home = TestHome.Create();
        var configFile = Path.Combine(home.Path, "settings.json");
        await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(new Dictionary<string, object> { [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal, [IntoChatOSHostingOptions.LocalModelKey] = "qwen3.5:2b" }), TestContext.Current.CancellationToken);
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, LocalSetupKeys.Argument(LocalSetupKeys.ConfigFile, configFile), "--" + IntoChatOSNames.HomeKey + "=" + home.Path], TestContext.Current.CancellationToken);

        var kernel = Assert.Single(appHost.Resources.OfType<ProjectResource>(), resource => resource.Name == "OperatingSystem-a");
        var environment = await LocalProfileOllamaFacts.EnvironmentOf(appHost, kernel);
        Assert.Equal("qwen3.5:2b", environment.GetValueOrDefault(AIKeys.OllamaModels + ":IQwen35:Model"));
    }

    [Fact]
    public async Task AConfigurationFileOverridesTheTopologyModuleDefaults()
    {
        using var home = TestHome.Create();
        var configFile = Path.Combine(home.Path, "settings.json");
        await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(new Dictionary<string, object> { [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal, [IntoChatOSConfiguration.ModuleSection("postgres") + ":Enabled"] = true }), TestContext.Current.CancellationToken);
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, LocalSetupKeys.Argument(LocalSetupKeys.ConfigFile, configFile), "--" + IntoChatOSNames.HomeKey + "=" + home.Path], TestContext.Current.CancellationToken);

        var kernel = Assert.Single(appHost.Resources.OfType<ProjectResource>(), resource => resource.Name == "OperatingSystem-a");
        var environment = await LocalProfileOllamaFacts.EnvironmentOf(appHost, kernel);
        Assert.DoesNotContain(IntoChatOSConfiguration.ModuleSection("postgres") + ":Enabled", environment.Keys);
    }

    [Theory]
    [InlineData("local", "local")]
    [InlineData("local+cloud", "local+cloud")]
    [InlineData("local-cloud", "local+cloud")]
    [InlineData("cloud", "cloud")]
    public async Task TheSelectedTopologyReachesTheKernelAndMcp(string topology, string expected)
    {
        using var home = TestHome.Create();
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, "--" + IntoChatOSNames.TopologyKey + "=" + topology, "--" + IntoChatOSNames.HomeKey + "=" + home.Path], TestContext.Current.CancellationToken);

        foreach (var name in new[] { "OperatingSystem-a", "intochat-os-mcp-a" })
        {
            var resource = Assert.Single(appHost.Resources.OfType<ProjectResource>(), project => project.Name == name);
            var environment = await LocalProfileOllamaFacts.EnvironmentOf(appHost, resource);
            Assert.Equal(expected, environment[IntoChatOSNames.TopologyKey]);
        }
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData("local+cloud", true)]
    [InlineData("cloud", false)]
    public async Task TheMcpClientUsesTheTopologysClusteringEndpoints(string topology, bool local)
    {
        using var home = TestHome.Create();
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, "--" + IntoChatOSNames.TopologyKey + "=" + topology, "--" + IntoChatOSNames.HomeKey + "=" + home.Path], TestContext.Current.CancellationToken);
        var kernel = Assert.Single(appHost.Resources.OfType<ProjectResource>(), resource => resource.Name == "OperatingSystem-a");
        var gateway = Assert.Single(kernel.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == RuntimeHosting.OrleansGatewayEndpoint);
        gateway.AllocatedEndpoint = new AllocatedEndpoint(gateway, "127.0.0.1", 49321, EndpointBindingMode.SingleAddress, networkId: null);

        var mcp = Assert.Single(appHost.Resources.OfType<ProjectResource>(), resource => resource.Name == "intochat-os-mcp-a");
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(appHost.ExecutionContext, mcp, environment, TestContext.Current.CancellationToken);
        foreach (var callback in mcp.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await callback.Callback(context); }
        var configuration = environment.ToDictionary(pair => IntoChatOSConfiguration.ConfigurationName(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(local ? "Development" : "AzureTableStorage", configuration["Orleans:Clustering:ProviderType"]);
        if (local)
        {
            Assert.True(configuration.TryGetValue("Orleans:Clustering:Gateways:0", out var value));
            var address = await Assert.IsAssignableFrom<IValueProvider>(value).GetValueAsync(TestContext.Current.CancellationToken);
            Assert.Equal("127.0.0.1:49321", address);
        }
        else
        {
            Assert.DoesNotContain(configuration.Keys, key => key.StartsWith("Orleans:Clustering:Gateways:", StringComparison.Ordinal));
        }
    }

    private const string MasterKey = "Parameters:Modules-intochat-os-master-key=synthetic-composition-test-key-32chars";

    private static bool IsAzurite(IResource resource) => resource is AzureStorageResource;

    [Fact]
    public void ALeanBrainSkipsEveryHostingTheLocalRunDoesNotNeed()
    {
        Assert.True(RunProfile.LocalLean.Lean);
        Assert.False(RunProfile.Cloud.Lean);
        Assert.False(RunProfile.Local.Lean);
    }

    [Fact]
    public async Task TheProductEntryPointAppliesCloudAndKeepsTheMasterKey()
    {
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            [MasterKey, "--" + IntoChatOSNames.TopologyKey + "=" + IntoChatOSNames.TopologyCloud], TestContext.Current.CancellationToken);
        Assert.Contains(appHost.Resources, resource => resource.Name == "Modules-intochat-os-master-key");
        Assert.DoesNotContain(appHost.Resources, resource => resource.Name.Contains("gmail-client", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(appHost.Resources, resource => resource.Name.Contains("salesforce-consumer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheReferenceOverlayCarriesRunProfileReference()
    {
        var overlaid = new AIOptions();
        ReferenceBrain.ApplyReferenceAi(overlaid);
        var reference = RunProfile.Reference.Ai;
        Assert.Equal(reference.Default.Model, overlaid.Default.Model);
        Assert.Equal(reference.Default.Embedding, overlaid.Default.Embedding);
        Assert.Equal(reference.Default.Transcription, overlaid.Default.Transcription);
        Assert.Equal(reference.Default.SystemOne, overlaid.Default.SystemOne);
        Assert.Equal(reference.Tavily.Enabled, overlaid.Tavily.Enabled);
        Assert.Equal(reference.OpenAI.Endpoint, overlaid.OpenAI.Endpoint);
        Assert.Equal(reference.Hosting.Llms, overlaid.Hosting.Llms);
        Assert.Equal(reference.Hosting.Embeddings, overlaid.Hosting.Embeddings);
        Assert.Equal(reference.Hosting.SystemOnes, overlaid.Hosting.SystemOnes);
    }
}
