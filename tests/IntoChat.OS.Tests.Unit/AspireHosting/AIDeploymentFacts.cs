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
        Assert.Equal(device, plan.Ai.Roles.Device.ContainsKey(ModelRole.Chat));
        Assert.Equal(cloud, plan.Ai.Roles.Cloud.ContainsKey(ModelRole.Chat));
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
        Assert.Equal("qwen3:14b", plan.Ai.Roles.Device[ModelRole.Chat]);
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
        Assert.Equal("gemma4:e2b", configured.Roles.Device[ModelRole.Chat]);
    }

    [Fact]
    public void LocalTopologyWithCloudOnlyResolvesCloudRolesWithoutDeviceDefaultsOrSearXng()
    {
        var deployment = new AIDeployment()
            .Cloud(c => c.Roles(r => r
                .Chat(KnownModels.DeepSeekV41Flash)
                .Think(KnownModels.Gpt56Luna)
                .Embed(KnownModels.TextEmbedding3Small)
                .Speak(KnownModels.Gpt4oMiniTts)
                .Talk(KnownModels.GptRealtime)))
            .Web(w => w.Brave());

        var plan = deployment.Resolve(Profile("local"), Config());

        Assert.Null(plan.Devices);
        Assert.Empty(plan.Ai.Roles.Device);
        Assert.Equal(KnownModels.DeepSeekV41Flash.Id.Value, plan.Ai.Roles.Cloud[ModelRole.Chat]);
        Assert.Equal(KnownModels.Gpt56Luna.Id.Value, plan.Ai.Roles.Cloud[ModelRole.Think]);
        Assert.Equal(KnownModels.TextEmbedding3Small.Id.Value, plan.Ai.Roles.Cloud[ModelRole.Embed]);
        Assert.Equal(KnownModels.Gpt4oMiniTts.Id.Value, plan.Ai.Roles.Cloud[ModelRole.Speak]);
        Assert.Equal(KnownModels.GptRealtime.Id.Value, plan.Ai.Roles.Cloud[ModelRole.Talk]);
        Assert.Equal(SearchProvider.Brave, plan.Web!.Provider);
        Assert.Null(plan.Web.SearxngUrl);

        // RoleMaps.For must NOT pre-fill Device defaults when only Cloud roles are configured.
        var roleMap = RoleMaps.For(plan.Ai);
        Assert.Empty(roleMap.Device);
        Assert.Equal(KnownModels.DeepSeekV41Flash.Id, roleMap.Cloud[ModelRole.Chat]);

        // RoleMaps.Deployment / Candidates / Explicit must resolve Cloud roles even under Topology=local.
        Assert.Equal([KnownModels.DeepSeekV41Flash.Id], RoleMaps.Deployment(roleMap, IntoChatOSNames.TopologyLocal, ModelRole.Chat).ToArray());
        Assert.Equal([KnownModels.GptRealtime.Id], RoleMaps.Candidates(roleMap, IntoChatOSNames.TopologyLocal, ModelRole.Talk).ToArray());
        Assert.Equal(KnownModels.DeepSeekV41Flash.Id, RoleMaps.Explicit(plan.Ai, IntoChatOSNames.TopologyLocal, ModelRole.Chat));
    }

    [Fact]
    public void LocalTopologyWithCloudOnlyAndSearXngPlusCloudOnlyBraveSelectsBrave()
    {
        var deployment = new AIDeployment()
            .Cloud(c => c.Roles(r => r.Chat(KnownModels.DeepSeekV41Flash)))
            .Web(w => w.SearXng().MicrosoftLearn().Brave(cloudOnly: true));

        var plan = deployment.Resolve(Profile("local"), Config());

        Assert.Null(plan.Devices);
        Assert.Equal(SearchProvider.Brave, plan.Web!.Provider);
        Assert.Null(plan.Web.SearxngUrl);
        Assert.True(plan.Mcp!.Servers["microsoft-learn"].Enabled);
    }

    [Fact]
    public void LocalTopologyWithCloudOnlySuppressesLocalDeviceRuntimesInAppHost()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--IntoChatOS:Topology=local", "--IntoChatOS:Shell:Enabled=false", "--IntoChatOS:Ollama:AutoContainer=false"],
            DisableDashboard = true,
        });

        var os = builder.AddIntoChatOS("Modules")
            .WithAI(ai => ai
                .Cloud(c => c.Roles(r => r.Chat(KnownModels.DeepSeekV41Flash).Embed(KnownModels.TextEmbedding3Small)))
                .Web(w => w.Brave()));

        Assert.DoesNotContain("devices", os.Modules);
        Assert.Equal("false", os.Settings["IntoChatOS:Modules:devices:Options:Ollama:Enabled"]);
        Assert.Equal("false", os.Settings["IntoChatOS:Modules:devices:Options:FoundryLocal:Enabled"]);
        Assert.Equal("false", os.Settings["IntoChatOS:Modules:devices:Options:SherpaOnnx:Enabled"]);
        Assert.DoesNotContain(os.ApplicationBuilder.Resources, static r => r.Name.Contains("ollama", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(os.ApplicationBuilder.Resources, static r => r.Name.Contains("searxng", StringComparison.OrdinalIgnoreCase));
    }
}
