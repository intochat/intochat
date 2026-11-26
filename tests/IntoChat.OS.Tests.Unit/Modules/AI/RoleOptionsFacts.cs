using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class RoleOptionsFacts
{
    private static AIOptions Bind(params (string Key, string Value)[] entries)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(static e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<AIOptions>().Bind(config.GetSection(AIKeys.Options));
        services.AddSingleton<IPostConfigureOptions<AIOptions>, LegacyDefaultsAlias>();
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IOptions<AIOptions>>().Value;
    }

    [Fact]
    public void LegacyDefaultModelBecomesTheChatRole()
    {
        var ai = Bind(("IntoChatOS:Modules:ai:Options:Default:Model", "IGpt56Luna"));
        Assert.Equal("gpt-5.6-luna", ai.Roles.Device[ModelRole.Chat]);
        Assert.Equal("gpt-5.6-luna", ai.Roles.Cloud[ModelRole.Chat]);
    }

    [Fact]
    public void AnExplicitRoleWinsOverTheLegacyKey()
    {
        var ai = Bind(
            ("IntoChatOS:Modules:ai:Options:Default:Model", "IGpt56Luna"),
            ("IntoChatOS:Modules:ai:Options:Roles:Cloud:Chat", "claude-sonnet-5"));
        Assert.Equal("claude-sonnet-5", ai.Roles.Cloud[ModelRole.Chat]);
        Assert.False(ai.Roles.Device.ContainsKey(ModelRole.Chat));
    }

    [Fact]
    public void LegacyDefaultsForAllKindsMapToTheirRolesAndUnknownsAreIgnored()
    {
        var ai = Bind(
            ("IntoChatOS:Modules:ai:Options:Default:Embedding", "ITextEmbedding3Small"),
            ("IntoChatOS:Modules:ai:Options:Default:Transcription", "IWhisperLargeV3Turbo"),
            ("IntoChatOS:Modules:ai:Options:Default:SystemOne", "IJev"),
            ("IntoChatOS:Modules:ai:Options:Default:Image", "IGptImage1"),
            ("IntoChatOS:Modules:ai:Options:Default:Model", "INonExistentModel"));

        Assert.False(ai.Roles.Device.ContainsKey(ModelRole.Chat));
        Assert.False(ai.Roles.Cloud.ContainsKey(ModelRole.Chat));
        Assert.Equal("text-embedding-3-small", ai.Roles.Cloud[ModelRole.Embed]);
        Assert.Equal("whisper-large-v3-turbo", ai.Roles.Device[ModelRole.Hear]);
        Assert.Equal("jev-latest", ai.Roles.Cloud[ModelRole.Fast]);
        Assert.Equal("gpt-image-1", ai.Roles.Cloud[ModelRole.Image]);
    }

    [Fact]
    public void WithRoleSetsTheMapAndHostsTheModel()
    {
        var ai = new AIOptions().WithRole(ModelRole.Chat, KnownModels.DeepSeekV41Flash);
        Assert.Equal("deepseek/deepseek-v4.1-flash", ai.Roles.Cloud[ModelRole.Chat]);
        Assert.Contains("deepseek/deepseek-v4.1-flash", ai.Hosting.Llms);
    }

    [Fact]
    public void WithoutLocalModelsClearsDeviceRoles() =>
        Assert.Empty(new AIOptions().WithRole(ModelRole.Chat, KnownModels.Qwen35, Where.Device).WithoutLocalModels().Roles.Device);

    [Fact]
    public void OldHostingMarkerNamesStillBind() =>
        Assert.Equal(KnownModels.Gpt56Luna, KnownModels.Find(Bind(("IntoChatOS:Modules:ai:Options:Hosting:Llms:0", "IGpt56Luna")).Hosting.Llms[0]));
}
