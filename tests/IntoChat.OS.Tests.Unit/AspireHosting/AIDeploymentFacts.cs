using Aspire.Hosting;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class AIDeploymentFacts
{
    private static AIDeployment Chain() => new AIDeployment()
        .Device(d => d.Ollama().FoundryLocal().Attach("lmstudio", new Uri("http://localhost:1234/v1"))
            .Roles(r => r.Chat(KnownModels.Qwen35).Think(KnownModels.Qwen25Coder14B).Hear(KnownModels.WhisperLargeV3Turbo))
            .Prefetch(KnownModels.Qwen35))
        .Cloud(c => c.Roles(r => r.Chat(KnownModels.DeepSeekV41Flash).Think(KnownModels.Gpt56Luna).Embed(KnownModels.TextEmbedding3Small)))
        .Web(w => w.SearXng().MicrosoftLearn().Brave(cloudOnly: true));

    private static DeploymentProfile Profile(string topology)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [$"--IntoChatOS:Topology={topology}", "--IntoChatOS:Shell:Enabled=false"],
            DisableDashboard = true,
        });
        return DeploymentProfile.From(builder);
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(static p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Theory]
    [InlineData("local", true, false, SearchProvider.Searxng)]
    [InlineData("local+cloud", true, true, SearchProvider.Searxng)]
    [InlineData("cloud", false, true, SearchProvider.Brave)]
    public void TopologyPicksTheSide(string topology, bool device, bool cloud, SearchProvider search)
    {
        var plan = Chain().Resolve(Profile(topology), Config());
        Assert.Equal(device, plan.Ai.Roles.Device.ContainsKey(Role.Chat));
        Assert.Equal(cloud, plan.Ai.Roles.Cloud.ContainsKey(Role.Chat));
        Assert.Equal(device, plan.Devices is not null);
        Assert.Equal(cloud, plan.Ai.Hosting.Llms.Contains(KnownModels.Gpt56Luna.Id.Value));
        Assert.Equal(search, plan.Web!.Provider);
    }

    [Fact]
    public void LocalHostsNoCloudModel()
        => Assert.Empty(Chain().Resolve(Profile("local"), Config()).Ai.Hosting.Llms);

    [Fact]
    public void HearIsPrefetchedAfterTheExplicitCards()
        => Assert.Equal(["qwen3.5:4b", "whisper-large-v3-turbo"], Chain().Resolve(Profile("local"), Config()).Devices!.Prefetch);

    [Fact]
    public void AttachedServersAreDeclared()
        => Assert.Equal("http://localhost:1234/v1", Chain().Resolve(Profile("local"), Config()).Devices!.Attach["lmstudio"].Url);

    [Fact]
    public void TheLauncherModelSetsDeviceChat()
    {
        var plan = Chain().Resolve(Profile("local"), Config((IntoChatOSHostingOptions.LocalModelKey, "qwen3:14b")));
        Assert.Equal("qwen3:14b", plan.Ai.Roles.Device[Role.Chat]);
        Assert.Contains("qwen3:14b", plan.Devices!.Prefetch);
    }

    [Fact]
    public void ACardWithoutTheAbilityIsRefused()
        => Assert.Contains("Hear", Assert.Throws<ArgumentException>(() => new RoleBindings().Hear(KnownModels.Qwen35)).Message);

    [Fact]
    public void ACloudCardCannotBeADeviceRole()
        => Assert.Throws<ArgumentException>(() => new AIDeployment().Device(d => d.Roles(r => r.Chat(KnownModels.Gpt56Luna))).Resolve(Profile("local"), Config()));

    [Fact]
    public void ADeviceCardCannotBeACloudRole()
        => Assert.Throws<ArgumentException>(() => new AIDeployment().Cloud(c => c.Roles(r => r.Chat(KnownModels.Qwen35))).Resolve(Profile("cloud"), Config()));

    [Fact]
    public void LearnIsOffWhenNotStated()
        => Assert.False(new AIDeployment().Web(w => w.SearXng()).Resolve(Profile("local"), Config()).Mcp!.Servers["microsoft-learn"].Enabled);

    [Fact]
    public void TheKernelAloneStillDefaultsTheLocalEndpoint()
    {
        var services = new ServiceCollection();
        var config = Config(
            (IntoChatOSNames.TopologyKey, IntoChatOSNames.TopologyLocal),
            (IntoChatOSNames.LocalModelKey, "gemma4:e2b"));
        services.AddSingleton(config);
        services.AddOptions<AIOptions>();
        AIClients.Add(services);

        using var provider = services.BuildServiceProvider();
        var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AIOptions>>().Value;
        Assert.Equal(IntoChatOSNames.LocalOllamaEndpoint, configured.Ollama.Endpoint);
        Assert.Equal("gemma4:e2b", configured.Roles.Device[Role.Chat]);
    }
}
