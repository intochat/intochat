using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps.Social;

namespace IntoChat.OS.Tests.Unit.Modules.Workspace.Social;

public sealed class RoomAgentDefinitionFacts
{
    [Fact]
    public void RoomAgentDefinitionEnforcesStrictPrivateBrainIsolation()
    {
        var definition = RoomAgentDefinition.Create("Trip Planning");

        Assert.Equal(RoomAgentDefinition.DisplayName, definition.DisplayName);
        Assert.Empty(definition.Contexts);
        Assert.False(definition.IncludeComponentOperations);
        Assert.Null(definition.OperationApps);
        Assert.Null(definition.ToolSources);
        Assert.Equal([typeof(IRoom).FullName!], definition.Contracts);
        Assert.DoesNotContain(definition.Contracts, c => c.Contains("IFiles", StringComparison.Ordinal));
        Assert.DoesNotContain(definition.Contracts, c => c.Contains("IMemory", StringComparison.Ordinal));
        Assert.DoesNotContain(definition.Contracts, c => c.Contains("IUserSettings", StringComparison.Ordinal));
        Assert.DoesNotContain(definition.Contracts, c => c.Contains("IStudio", StringComparison.Ordinal));
        Assert.Equal(SenderKind.Agent, RoomAgentDefinition.AgentSender.Kind);
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
        Assert.Equal(expected, RoomAgentDefinition.IsMentioned(text));
    }
}
