using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class ToolPolicyFacts
{
    [Fact]
    public void SourcesForReturnsOrderedDistinctBoundKeysForDeclaredAgentToolSlotsOnly()
    {
        var manifest = new AppManifest(
            Title: "Treg",
            Description: "Treg helper",
            Operations: [],
            Settings: [],
            Slots:
            [
                new AppSlot("treg", Connector: "mcp", Required: true),
                new AppSlot("docs", Connector: "mcp", Required: false),
                new AppSlot("unused", Connector: "mcp", Required: false),
            ],
            Agent: new AppAgent(
                Instructions: "Use treg and docs.",
                Tools: ["treg", "docs"]));

        var bound = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["treg"] = "brain/connection/mcp/treg-1",
            ["docs"] = "brain/connection/mcp/docs-1",
            ["unused"] = "brain/connection/mcp/secret-other",
            ["undeclared"] = "brain/connection/mcp/rogue",
        };

        var sources = ToolPolicy.SourcesFor(manifest, bound);

        Assert.Equal(["brain/connection/mcp/treg-1", "brain/connection/mcp/docs-1"], sources);
    }

    [Fact]
    public void SourcesForReturnsEmptyForNonAgentOrUnboundManifest()
    {
        var nonAgent = new AppManifest("Notes", "Notes", [], [], []);
        Assert.Empty(ToolPolicy.SourcesFor(nonAgent, new Dictionary<string, string> { ["x"] = "brain/connection/mcp/x" }));

        var agent = new AppManifest(
            "Treg",
            "Treg",
            [],
            [],
            [new AppSlot("treg", Connector: "mcp", Required: true)],
            Agent: new AppAgent("Help", Tools: ["treg"]));
        Assert.Empty(ToolPolicy.SourcesFor(agent, null));
        Assert.Empty(ToolPolicy.SourcesFor(agent, new Dictionary<string, string>()));
    }

    [Fact]
    public void AppsForAssistantMergesAlwaysOnOpenAndConversationAppsInOrderWithoutDuplicates()
    {
        var result = ToolPolicy.AppsForAssistant(
            alwaysOn: ["ino", "files"],
            openApps: ["apps/files", "app.tasks", " "],
            openedByConversation: ["local/apps/treg", "TASKS", "notes"]);

        Assert.Equal(["ino", "files", "tasks", "treg", "notes"], result);
    }

    [Fact]
    public void OpenAppsFromWorkspaceExtractsNormalizedDistinctAppsAcrossViewsWindowsAndOpenList()
    {
        var workspace = new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState(
            Revision: 3,
            Path: [],
            Open: ["apps/notes", "tasks"])
        {
            OpenViews =
            [
                new("v1", "local/apps/tasks", "full", ""),
                new("v2", "apps/files", "full", ""),
            ],
            Windows =
            [
                new("w1", [new("v3", "app.calendar", "full", ""), new("v1", "apps/tasks", "full", "")], "v3"),
            ],
        };

        var open = ToolPolicy.OpenAppsFromWorkspace(workspace);

        Assert.Equal(["tasks", "files", "calendar", "notes"], open);
        Assert.Empty(ToolPolicy.OpenAppsFromWorkspace(null));
    }

    [Fact]
    public void TryExtractLaunchedAppIdHandlesLaunchToolsAndIgnoresOtherTools()
    {
        var fromString = ToolPolicy.TryExtractLaunchedAppId(
            "studio_launch",
            new Dictionary<string, object?> { ["appId"] = "apps/tasks" });
        Assert.Equal("tasks", fromString);

        var fromJson = ToolPolicy.TryExtractLaunchedAppId(
            "desktop_launch",
            new Dictionary<string, object?> { ["app"] = System.Text.Json.JsonSerializer.SerializeToElement("app.notes") });
        Assert.Equal("notes", fromJson);

        Assert.Null(ToolPolicy.TryExtractLaunchedAppId(
            "files_open",
            new Dictionary<string, object?> { ["appId"] = "tasks" }));
    }
}
