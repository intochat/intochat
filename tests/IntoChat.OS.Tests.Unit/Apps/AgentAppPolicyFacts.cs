using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Tests.Unit.Apps;

public sealed class AgentAppPolicyFacts
{
    [Fact]
    public void ConversationOfIsDisjointFromInoSessionIds()
    {
        var convId = AgentAppPolicy.ConversationOf("treg");
        Assert.Equal("agent-treg", convId.Value);
        Assert.False(Sessions.IsId(convId.Value));
        Assert.Equal("agent-treg", AgentAppPolicy.AgentIdOf("treg"));
    }

    [Fact]
    public void DefinitionBuildsIsolatedAgentDefinitionWithBoundToolSourcesOnly()
    {
        var manifest = new AppManifest(
            Title: "Treg",
            Description: "Track tasks",
            Operations: [],
            Settings: [],
            Slots:
            [
                new AppSlot("treg", Connector: "mcp", Required: true),
                new AppSlot("extra", Connector: "mcp", Required: false),
            ],
            Agent: new AppAgent(
                Instructions: "Help with treg.",
                Model: new ModelSelection("gpt-5.4"),
                Starters: ["What is open?"],
                Tools: ["treg"]));

        var bound = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["treg"] = "b1/connection/mcp/treg-main",
            ["extra"] = "b1/connection/mcp/other",
        };

        var def = AgentAppPolicy.Definition(manifest, bound);

        Assert.Equal("Treg", def.DisplayName);
        Assert.Equal("Track tasks", def.Description);
        Assert.Equal("Help with treg.", def.Instructions);
        Assert.Equal(new ModelSelection(Model: "gpt-5.4"), def.Model);
        Assert.Empty(def.Contracts);
        Assert.False(def.IncludeComponentOperations);
        Assert.NotNull(def.ToolSources);
        Assert.Equal(["b1/connection/mcp/treg-main"], def.ToolSources);
    }

    [Fact]
    public void SameSeatDetectsChangesInInstructionsModelAndToolSources()
    {
        var a = new AgentDefinition
        {
            DisplayName = "Treg",
            Description = "d",
            Instructions = "i1",
            Model = new ModelSelection("gpt-5.4"),
            Contracts = [],
            IncludeComponentOperations = false,
            ToolSources = ["b1/connection/mcp/a"],
        };

        Assert.False(AgentAppPolicy.SameSeat(null, a));
        Assert.True(AgentAppPolicy.SameSeat(a, a with { ToolSources = ["b1/connection/mcp/a"] }));
        Assert.False(AgentAppPolicy.SameSeat(a, a with { Instructions = "i2" }));
        Assert.False(AgentAppPolicy.SameSeat(a, a with { Model = new ModelSelection("claude-3.7") }));
        Assert.False(AgentAppPolicy.SameSeat(a, a with { ToolSources = ["b1/connection/mcp/b"] }));
        Assert.False(AgentAppPolicy.SameSeat(a, a with { IncludeComponentOperations = true }));
    }
}
