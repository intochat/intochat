using IntoChat.OS.Modules.AI.Agents;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class AgentDefinitionRulesFacts
{
    [Fact]
    public void ValidateAcceptsDefaultDefinition()
    {
        AgentDefinitionRules.Validate(new AgentDefinition());
    }

    [Fact]
    public void ValidateRejectsOutOfRangeLimits()
    {
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { MaxModelCalls = 0 }));
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { Timeout = TimeSpan.FromMinutes(61) }));
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { MaxHistoryMessages = 1 }));
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { CompactionThreshold = 1 }));
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { CompactionRecentWindow = 0 }));
    }

    [Fact]
    public void ValidateRejectsInvalidCollections()
    {
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { Contracts = ["dup", "dup"] }));
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { Contexts = [" "] }));
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { OperationApps = ["app", "APP"] }));
        Assert.Throws<ArgumentException>(() => AgentDefinitionRules.Validate(new AgentDefinition { ToolSources = ["source-a", "source-a"] }));
    }

    [Fact]
    public void NormalizeCopiesArraysDefensivelyAndPreservesEquality()
    {
        string[] contracts = ["c1"];
        string[] contexts = ["context/a"];
        string[] operationApps = ["app.a"];
        string[] toolSources = ["tool.a"];
        var original = new AgentDefinition
        {
            DisplayName = "Agent",
            Instructions = "Be helpful.",
            Contracts = contracts,
            Contexts = contexts,
            OperationApps = operationApps,
            ToolSources = toolSources,
        };

        var normalized = AgentDefinitionRules.Normalize(original);
        Assert.True(AgentDefinitionRules.AreEqual(original, normalized));

        contracts[0] = "mutated";
        operationApps[0] = "mutated";
        Assert.Equal(["c1"], normalized.Contracts);
        Assert.NotNull(normalized.OperationApps);
        Assert.Equal(["app.a"], normalized.OperationApps);
        Assert.False(AgentDefinitionRules.AreEqual(original, normalized));
    }
}
