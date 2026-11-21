using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class UserSettingsRoleFacts
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OldDefaultModelReadsAsChatRole()
    {
        var state = JsonSerializer.Deserialize<UserSettingsState>(
            """{"displayName":"A","theme":"dark","defaultModel":"IQwen35","variables":[]}""",
            Web)!;

        Assert.Equal("qwen3.5:4b", state.RoleOf(Role.Chat)!.Value.Value);
        Assert.Null(state.RoleOf(Role.Think));
    }

    [Fact]
    public async Task SetRoleChatAlsoWritesDefaultModel()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local();
        os.Register<UserSettingsNeuron>();

        var settings = os.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));
        var after = await settings.SetRole(Role.Chat, KnownModels.Sonnet5.Id);

        Assert.Equal("claude-sonnet-5", after.DefaultModel);
        Assert.Equal(KnownModels.Sonnet5.Id, after.RoleOf(Role.Chat));
    }

    [Fact]
    public async Task SetRoleThinkLeavesDefaultModelUntouchedAndReplacesExisting()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local();
        os.Register<UserSettingsNeuron>();

        var settings = os.Get<IUserSettings>(new NeuronId(new BrainId("local"), "os.settings", "settings"));
        await settings.SetRole(Role.Chat, KnownModels.Gpt54.Id);
        await settings.SetRole(Role.Think, KnownModels.Opus5.Id);
        var after = await settings.SetRole(Role.Think, KnownModels.Sonnet5.Id);

        Assert.Equal("gpt-5.4", after.DefaultModel);
        Assert.Equal(KnownModels.Gpt54.Id, after.RoleOf(Role.Chat));
        Assert.Equal(KnownModels.Sonnet5.Id, after.RoleOf(Role.Think));
        Assert.Equal(2, after.Roles!.Length);
    }
}
