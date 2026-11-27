using DigitalBrain.Invocation;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class AgentAppManifestFacts
{
    private static AppManifest ValidAgentManifest(
        AppAgent? agent = null,
        AppOperation[]? operations = null,
        AppSlot[]? slots = null,
        AppRequirements? requires = null)
        => new(
            Title: "Treg Assistant",
            Description: "Monitors treg workspace.",
            Operations: operations ?? [],
            Settings: [],
            Slots: slots ?? [new AppSlot("treg", Connector: "mcp", Required: true)],
            Requires: requires,
            Agent: agent ?? new AppAgent(
                Instructions: "You help manage treg issues.",
                Starters: ["List open tasks"],
                Tools: ["treg"]));

    [Fact]
    public void ValidAgentManifestPassesValidationAndAutoMergesRequiredSlotRequirements()
    {
        var validated = ValidAgentManifest(
            slots:
            [
                new AppSlot("treg", Connector: "mcp", Required: true),
                new AppSlot("files", Capability: "storage@1", Required: true),
            ],
            agent: new AppAgent(
                Instructions: "You help manage treg issues.",
                Starters: ["List open tasks"],
                Tools: ["treg"])).Validated();

        Assert.NotNull(validated.Agent);
        Assert.Equal("You help manage treg issues.", validated.Agent.Instructions);
        Assert.Equal(["List open tasks"], validated.Agent.Starters ?? []);
        Assert.Equal(["treg"], validated.Agent.Tools ?? []);
        Assert.NotNull(validated.Requires);
        Assert.Contains("mcp", validated.Requires.Connectors ?? []);
        Assert.Contains("storage@1", validated.Requires.Capabilities ?? []);
    }

    [Fact]
    public void RequiredConnectorSlotAutoMergesIntoRequiresConnectorsWithoutDuplicates()
    {
        var manifest = ValidAgentManifest(
            slots:
            [
                new AppSlot("gh", Connector: "github", Required: true),
                new AppSlot("optional_storage", Capability: "storage@1", Required: false),
            ],
            agent: new AppAgent("Help with GitHub.", Tools: ["gh"]),
            requires: new AppRequirements(Connectors: ["github"]));

        var validated = manifest.Validated();

        Assert.NotNull(validated.Requires);
        Assert.Equal(["github"], validated.Requires.Connectors ?? []);
        Assert.Null(validated.Requires.Capabilities);
    }

    [Fact]
    public void BlankInstructionsAreRejected()
    {
        var manifest = ValidAgentManifest(agent: new AppAgent("   "));
        Assert.Throws<InvalidDataException>(manifest.Validated);
    }

    [Fact]
    public void AgentAppWithDeclaredOperationsIsRejected()
    {
        var manifest = ValidAgentManifest(operations: [new AppOperation("ping", "Ping")]);
        Assert.Throws<InvalidDataException>(manifest.Validated);
    }

    [Fact]
    public void BlankStarterEntryIsRejected()
    {
        var manifest = ValidAgentManifest(agent: new AppAgent("Help", Starters: ["Good", "   "]));
        Assert.Throws<InvalidDataException>(manifest.Validated);
    }

    [Fact]
    public void AgentToolReferencingUndeclaredSlotIsRejected()
    {
        var manifest = ValidAgentManifest(
            slots: [new AppSlot("treg", Connector: "mcp", Required: true)],
            agent: new AppAgent("Help", Tools: ["missing_slot"]));
        Assert.Throws<InvalidDataException>(manifest.Validated);
    }

    [Fact]
    public void DuplicateSlotInAgentToolsIsRejected()
    {
        var manifest = ValidAgentManifest(
            slots: [new AppSlot("treg", Connector: "mcp", Required: true)],
            agent: new AppAgent("Help", Tools: ["treg", "treg"]));
        Assert.Throws<InvalidDataException>(manifest.Validated);
    }

    [Fact]
    public void NonAgentManifestOmitsAgentInJsonAndAgentManifestChangesDigest()
    {
        var plain = new AppManifest(
            Title: "Notes",
            Description: "Plain app",
            Operations: [],
            Settings: [],
            Slots: []);

        var json = RuntimeProtocol.SerializeToElement(plain, typeof(AppManifest)).GetRawText();
        Assert.DoesNotContain("\"agent\"", json, StringComparison.OrdinalIgnoreCase);

        var withAgent = plain with { Agent = new AppAgent("Be helpful.") };
        var roundTripped = RuntimeProtocol.Deserialize(
            RuntimeProtocol.SerializeToElement(withAgent, typeof(AppManifest)),
            typeof(AppManifest)) as AppManifest;

        Assert.NotNull(roundTripped?.Agent);
        Assert.Equal("Be helpful.", roundTripped.Agent.Instructions);

        var plainBundle = new AppBundle("notes", "1.0.0", plain, "spec", new());
        var agentBundle = new AppBundle("notes", "1.0.0", withAgent, "spec", new());
        Assert.NotEqual(AppPublicationDigest.Of(plainBundle), AppPublicationDigest.Of(agentBundle));
    }

    [Fact]
    public void SpecialistAgentFieldsNormalizeValidateAndRoundTripAsConcreteArrays()
    {
        var manifest = new AppManifest(
            Title: "Code Reviewer",
            Description: "Reviews PRs",
            Operations: [],
            Settings: [],
            Slots: [],
            Agent: new AppAgent(
                Instructions: "Review diffs carefully.",
                Handle: " Reviewer ",
                AllowedContracts: ["IntoChat.OS.Modules.Microsoft.GitHub.IRepository"],
                ToolSources: ["b1/connection/mcp/github"],
                MemoryScope: MemoryScope.IsolatedBotScope)).Validated();

        Assert.Equal("@reviewer", manifest.Agent!.Handle);
        Assert.Equal(["IntoChat.OS.Modules.Microsoft.GitHub.IRepository"], manifest.Agent.AllowedContracts ?? []);
        Assert.Equal(["b1/connection/mcp/github"], manifest.Agent.ToolSources ?? []);
        Assert.Equal(MemoryScope.IsolatedBotScope, manifest.Agent.MemoryScope);

        var roundTripped = RuntimeProtocol.Deserialize(
            RuntimeProtocol.SerializeToElement(manifest, typeof(AppManifest)),
            typeof(AppManifest)) as AppManifest;

        Assert.NotNull(roundTripped);
        Assert.Equal(manifest.Title, roundTripped.Title);
        Assert.Equal(manifest.Description, roundTripped.Description);
        Assert.Equal(manifest.Agent, roundTripped.Agent);
    }

    [Fact]
    public void ManifestValidationRejectsReservedHandleAndDisallowedContracts()
    {
        var reserved = ValidAgentManifest(agent: new AppAgent("Help", Handle: "@ino"));
        Assert.Throws<InvalidDataException>(reserved.Validated);

        var disallowed = ValidAgentManifest(agent: new AppAgent(
            "Help",
            Handle: "@helper",
            AllowedContracts: ["DigitalBrain.Capabilities.Secrets.ISecretVault"]));
        Assert.Throws<InvalidDataException>(disallowed.Validated);
    }
}
