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
        Assert.False(Threads.IsLocalId(convId.Value));
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
        Assert.False(AgentAppPolicy.SameSeat(a, a with { MemoryScopeKey = "memory/bots/treg" }));
    }

    [Theory]
    [InlineData("reviewer", "@reviewer")]
    [InlineData("@Reviewer", "@reviewer")]
    [InlineData("  @sql-analyst  ", "@sql-analyst")]
    [InlineData("planner2", "@planner2")]
    public void HandleNormalizationAcceptsValidHandles(string input, string expected)
    {
        Assert.Equal(expected, AgentAppPolicy.NormalizeHandle(input));
        Assert.True(AgentAppPolicy.TryNormalizeHandle(input, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@")]
    [InlineData("@a")]
    [InlineData("@1bot")]
    [InlineData("@bad handle")]
    [InlineData("@bot-")]
    [InlineData("@bad--bot")]
    [InlineData("@../escape")]
    [InlineData("@ino")]
    [InlineData("ino")]
    [InlineData("@INO")]
    public void HandleNormalizationRejectsInvalidAndReservedInoHandles(string? input)
    {
        Assert.Throws<InvalidDataException>(() => AgentAppPolicy.NormalizeHandle(input));
        Assert.False(AgentAppPolicy.TryNormalizeHandle(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void EnsureUniqueHandleRejectsDuplicateAcrossDifferentSpecialistsAndAllowsSelfUpdate()
    {
        var existing = new (string AppId, string? Handle)[]
        {
            ("reviewer", "@reviewer"),
            ("planner", "@planner"),
        };

        AgentAppPolicy.EnsureUniqueHandle("@reviewer", "reviewer", existing);
        AgentAppPolicy.EnsureUniqueHandle("@sql-analyst", "sql-analyst", existing);

        var ex = Assert.Throws<InvalidDataException>(() =>
            AgentAppPolicy.EnsureUniqueHandle("@reviewer", "other-bot", existing));
        Assert.Contains("@reviewer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedContractsAllowlistPermitsCuratedLeastPrivilegeContractsOnly()
    {
        foreach (var curated in AgentAppPolicy.CuratedAllowedContracts)
        {
            Assert.True(AgentAppPolicy.IsAllowedContract(curated));
        }

        var validated = AgentAppPolicy.ValidateAllowedContracts(
            ["IntoChat.OS.Modules.Web.IWebSearch", "IntoChat.OS.Modules.Apps.IData"]);
        Assert.Equal(["IntoChat.OS.Modules.Web.IWebSearch", "IntoChat.OS.Modules.Apps.IData"], validated);
    }

    [Theory]
    [InlineData("IntoChat.OS.Modules.Identity.Contracts.Grants.IGrantsNeuron")]
    [InlineData("IntoChat.OS.Modules.Identity.Contracts.IBrainOwners")]
    [InlineData("DigitalBrain.Capabilities.Secrets.ISecretVault")]
    [InlineData("IntoChat.OS.Host.Custody.IMasterKeyCustody")]
    [InlineData("IntoChat.OS.Modules.Apps.IUserSettings")]
    [InlineData("DigitalBrain.Confirmation.IConfirmations")]
    [InlineData("IntoChat.OS.Modules.Devices.IRemotePcResource")]
    [InlineData("IntoChat.OS.Modules.Apps.Flows.ICodeOrchestration")]
    [InlineData("IntoChat.OS.Modules.AI.Memory.IMemory")]
    [InlineData("IntoChat.OS.Modules.Apps.IWorkspaceFiles")]
    [InlineData("System.Object")]
    public void AllowedContractsDeniesIdentitySecretsCustodyAndNonCuratedContractsFailClosed(string disallowedContract)
    {
        Assert.False(AgentAppPolicy.IsAllowedContract(disallowedContract));
        Assert.Throws<UnauthorizedAccessException>(() =>
            AgentAppPolicy.ValidateAllowedContracts([disallowedContract]));

        // Even when mixed with a curated contract, policy fails closed and never silently widens or strips.
        Assert.Throws<UnauthorizedAccessException>(() =>
            AgentAppPolicy.ValidateAllowedContracts(["IntoChat.OS.Modules.Web.IWebSearch", disallowedContract]));

        var manifest = new AppManifest(
            Title: "Escalator",
            Description: "Attempts escalation",
            Operations: [],
            Settings: [],
            Slots: [],
            Agent: new AppAgent("Try to escalate.", AllowedContracts: [disallowedContract]));

        Assert.Throws<UnauthorizedAccessException>(() => AgentAppPolicy.Definition(manifest, null, "escalator"));
    }

    [Fact]
    public void IsolatedBotScopeConfinesPathsAndEnforcesCrossBotIsolation()
    {
        Assert.Equal("memory/bots/reviewer", BotMemoryScope.RootFor("reviewer"));
        Assert.Equal("memory/bots/reviewer/notes/pr-42.md", BotMemoryScope.ResolveConfinedPath("reviewer", "notes/pr-42.md"));
        Assert.True(BotMemoryScope.CanAccess("reviewer", "memory/bots/reviewer"));
        Assert.True(BotMemoryScope.CanAccess("reviewer", "memory/bots/reviewer/notes/pr-42.md"));

        // Cross-bot, host memory, and host secrets are strictly denied.
        Assert.False(BotMemoryScope.CanAccess("reviewer", "memory/bots/planner"));
        Assert.False(BotMemoryScope.CanAccess("reviewer", "memory/bots/planner/notes.md"));
        Assert.False(BotMemoryScope.CanAccess("reviewer", "memory"));
        Assert.False(BotMemoryScope.CanAccess("reviewer", "secrets/master-key"));
        Assert.False(BotMemoryScope.CanAccess("reviewer", "memory/bots/reviewer/../planner/secret.md"));

        // Traversal and escape paths throw UnauthorizedAccessException.
        Assert.Throws<UnauthorizedAccessException>(() => BotMemoryScope.ResolveConfinedPath("reviewer", "../planner/notes.md"));
        Assert.Throws<UnauthorizedAccessException>(() => BotMemoryScope.ResolveConfinedPath("reviewer", "%2e%2e/planner/notes.md"));
        Assert.Throws<UnauthorizedAccessException>(() => BotMemoryScope.ResolveConfinedPath("reviewer", "/etc/passwd"));
        Assert.Throws<UnauthorizedAccessException>(() => BotMemoryScope.ResolveConfinedPath("reviewer", "..\\..\\secrets"));
        Assert.Throws<UnauthorizedAccessException>(() => BotMemoryScope.ResolveConfinedPath("reviewer", "notes/../../host"));
    }

    [Fact]
    public void DefinitionProjectsIsolatedBotScopeAndDirectToolSources()
    {
        var manifest = new AppManifest(
            Title: "SQL Analyst",
            Description: "Queries datasets",
            Operations: [],
            Settings: [],
            Slots: [],
            Agent: new AppAgent(
                Instructions: "Analyze SQL.",
                Handle: "@sql-analyst",
                AllowedContracts: ["IntoChat.OS.Modules.Apps.IData"],
                ToolSources: ["b1/connection/mcp/sql"],
                MemoryScope: MemoryScope.IsolatedBotScope)).Validated();

        var def = AgentAppPolicy.Definition(manifest, null, "sql-analyst", brainId: "b1");

        Assert.Equal(["IntoChat.OS.Modules.Apps.IData"], def.Contracts);
        Assert.Equal(["b1/connection/mcp/sql"], def.ToolSources ?? []);
        Assert.Equal("memory/bots/sql-analyst", def.MemoryScopeKey);
        Assert.Equal(["context/bots/sql-analyst/context/memory.recall"], def.Contexts);
        Assert.Equal(
            "b1/context/bots/sql-analyst/context/memory.recall",
            IntoChat.OS.Modules.AI.Agents.AgentTurnLoop.Absolute("b1", def.Contexts[0]));
        Assert.False(def.IncludeComponentOperations);

        // Cross-brain tool source is denied fail-closed.
        Assert.Throws<UnauthorizedAccessException>(() => AgentAppPolicy.Definition(manifest, null, "sql-analyst", brainId: "other-brain"));
    }

    [Fact]
    public async Task ToolSourceToolsForKeysEnforcesSameBrainConfinementFailClosed()
    {
        var calledKeys = new List<string>();
        var tools = await IntoChat.OS.Modules.AI.Agents.Tools.ToolSourceTools.ForKeys(
            call: (_, key, _, _, _) =>
            {
                calledKeys.Add(key);
                return Task.FromResult<System.Text.Json.JsonElement?>(null);
            },
            sources:
            [
                ("IntoChat.OS.Modules.Mcp.IMcpConnection", "brain-a/connection/mcp/allowed"),
                ("IntoChat.OS.Modules.Mcp.IMcpConnection", "brain-b/connection/mcp/cross-brain-secret"),
            ],
            edge: null,
            conversation: "brain-a/conversations/agent-reviewer",
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(tools);
        Assert.Equal(["brain-a/connection/mcp/allowed"], calledKeys);
        Assert.DoesNotContain("brain-b/connection/mcp/cross-brain-secret", calledKeys);
    }

    [Fact]
    public void SpecialistAuthoringFailsClosedWithActionableErrorsOnInvalidInputs()
    {
        var existing = new (string AppId, string? Handle)[] { ("reviewer", "@reviewer") };

        var reserved = SpecialistAuthoring.PrepareCreate(
            new SpecialistDraft("Ino Clone", "@ino", "Be ino."),
            existing,
            brainId: "b1");
        Assert.False(reserved.Succeeded);
        Assert.Contains("reserved", reserved.Error, StringComparison.OrdinalIgnoreCase);

        var duplicate = SpecialistAuthoring.PrepareCreate(
            new SpecialistDraft("Reviewer Two", "@reviewer", "Review PRs."),
            existing,
            brainId: "b1");
        Assert.False(duplicate.Succeeded);
        Assert.Contains("already used", duplicate.Error, StringComparison.OrdinalIgnoreCase);

        var escalation = SpecialistAuthoring.PrepareCreate(
            new SpecialistDraft("Vault Reader", "@vault-bot", "Read vault.", AllowedContracts: ["DigitalBrain.Capabilities.Secrets.ISecretVault"]),
            existing,
            brainId: "b1");
        Assert.False(escalation.Succeeded);
        Assert.Contains("denied", escalation.Error, StringComparison.OrdinalIgnoreCase);

        var crossBrainTool = SpecialistAuthoring.PrepareCreate(
            new SpecialistDraft("Cross Brain Bot", "@xbrain", "Use other brain tools.", ToolSources: ["other-brain/connection/mcp/secret"]),
            existing,
            brainId: "b1");
        Assert.False(crossBrainTool.Succeeded);
        Assert.Contains("belongs to brain", crossBrainTool.Error, StringComparison.OrdinalIgnoreCase);

        var malformedSkill = SpecialistAuthoring.PrepareSkillImport(
            "bad/SKILL.md",
            "---\nname: broken\nno closing fence",
            existing,
            brainId: "b1");
        Assert.False(malformedSkill.Succeeded);
        Assert.Contains("closing '---'", malformedSkill.Error, StringComparison.OrdinalIgnoreCase);
    }
}
