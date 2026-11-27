using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Tests.Unit.Modules.Workspace.Social;

public sealed class RoomAgentDefinitionFacts
{
    [Fact]
    public void SharedRoomAgentPolicyEnforcesStrictPrivateBrainIsolation()
    {
        var definition = SharedRoomAgentPolicy.CreateRoomAgent("Trip Planning");

        Assert.Equal(SharedRoomAgentPolicy.DefaultRoomDisplayName, definition.DisplayName);
        Assert.Empty(definition.Contexts);
        Assert.False(definition.IncludeComponentOperations);
        Assert.Null(definition.OperationApps);
        Assert.Null(definition.ToolSources);
        Assert.Equal([typeof(IConversation).FullName!], definition.Contracts);
        Assert.DoesNotContain(definition.Contracts, c => c.Contains("IFiles", StringComparison.Ordinal));
        Assert.DoesNotContain(definition.Contracts, c => c.Contains("IMemory", StringComparison.Ordinal));
        Assert.DoesNotContain(definition.Contracts, c => c.Contains("IUserSettings", StringComparison.Ordinal));
        Assert.DoesNotContain(definition.Contracts, c => c.Contains("IStudio", StringComparison.Ordinal));
        Assert.Equal(SenderKind.Agent, SharedRoomAgentPolicy.RoomAgentSender.Kind);
    }

    [Fact]
    public void ConfineStripsPrivateContractsContextsAndComponentOperations()
    {
        var privileged = new AgentDefinition
        {
            DisplayName = "Custom Bot",
            Instructions = "Be helpful.",
            Contracts = ["IntoChat.OS.Modules.Apps.IFiles", "IntoChat.OS.Modules.AI.Memory.IMemory", typeof(IConversation).FullName!],
            Contexts = ["context/profile", "context/private"],
            IncludeComponentOperations = true,
            OperationApps = ["files", "settings"],
            ToolSources = ["mcp-private"],
        };

        var confined = SharedRoomAgentPolicy.Confine(privileged, "Shared Room");
        SharedRoomAgentPolicy.AssertConfined(confined);

        Assert.Empty(confined.Contexts);
        Assert.False(confined.IncludeComponentOperations);
        Assert.Null(confined.OperationApps);
        Assert.Null(confined.ToolSources);
        Assert.Equal([typeof(IConversation).FullName!], confined.Contracts);
    }

    [Theory]
    [InlineData("@Ino help us decide", true)]
    [InlineData("hey @ino, what do you think?", true)]
    [InlineData("Can you check (@Ino)?", true)]
    [InlineData("hello everyone", false)]
    [InlineData("email@ino.com", false)]
    [InlineData("piano lesson at 5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMentionedMatchesOnlyExplicitInoMentions(string? text, bool expected)
    {
        Assert.Equal(expected, SharedRoomAgentPolicy.IsMentioned(text));
    }
}
