using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Data;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.UI;

namespace IntoChat.OS.Tests.Unit.UI;

public sealed class OsAppsMixStyleFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void KitBuildersAndOsMixStylesEmitStructuredStyleWireJson()
    {
        var primary = Kit.PrimaryButton("Ship PR", "git-pull-request").Key("ship-pr").ToNode();
        var secondary = Kit.Button("Cancel").Key("cancel").Small().ToNode();
        var ghost = Kit.GhostButton("Dismiss").Key("dismiss").Small().ToNode();
        var danger = Kit.DangerButton("Delete", "trash-2").Key("delete").ToNode();
        var icon = Kit.IconButton("x", "Close").Key("close").ToNode();
        var card = Kit.Card("Summary", "Subtitle").Style(OsMixStyles.Card())[Kit.Text("Body")].ToNode();
        var surface = Kit.Surface(SurfaceKind.Elevated, "Panel").Style(OsMixStyles.Surface(SurfaceKind.Elevated))[Kit.Text("Body")].ToNode();

        var root = new Column([primary, secondary, ghost, danger, icon, card, surface]);
        var json = Serialize(root);
        using var doc = JsonDocument.Parse(json);

        var styles = CollectStyleObjects(doc.RootElement);
        Assert.Equal(7, styles.Count);

        Assert.All(styles, item =>
        {
            Assert.Equal(JsonValueKind.Object, item.Style.ValueKind);
            Assert.True(item.Style.TryGetProperty("tone", out _), $"Expected 'tone' in {item.Kind} style JSON.");
        });

        AssertNoLegacyButtonStyleStrings(doc.RootElement);
    }

    [Fact]
    public void FlagshipOsAppsEmitStructuredMixStylesWithZeroLegacyStringButtonStyles()
    {
        // 1. Ino (sessions acrylic surface + "+ New" button + model setup card + CTA)
        var inoNode = IntoChat.OS.Apps.Tests.Unit.Ino.InoDrawing.Screen(
            new InoAppState
            {
                Status = InoModel.Ready,
                ActiveSection = "chats",
                Error = new InoError("no-provider", "Connect a model"),
            },
            sessions: new IntoChat.OS.Modules.Flutter.Conversations.SessionPage(
                [new IntoChat.OS.Modules.Flutter.Conversations.ConversationSession("main", "Welcome", DateTimeOffset.UtcNow, false)],
                1)).Root;
        AssertStructuredMixStyles("Ino", inoNode, expectedMinStyles: 3);

        // 2. Engineer (top bar, workspace actions, inspector, links)
        var engineerNode = EngineerViewRenderer.RenderRoot(new EngineerWorkspaceState(), lastRejection: null);
        AssertStructuredMixStyles("Engineer", engineerNode, expectedMinStyles: 5);

        // 3. Settings (all 13 sections + no monospace regression)
        foreach (var entry in SettingsApp.SearchIndex)
        {
            var settingsNode = AggregateExplorer<SettingsAppState>.PreviewNode<SettingsApp>(new SettingsAppState().Navigate(entry.SectionId));
            var settingsWire = AssertStructuredMixStyles($"Settings:{entry.SectionId}", settingsNode, expectedMinStyles: 2);
            Assert.DoesNotContain("\"mono\":true", settingsWire, StringComparison.OrdinalIgnoreCase);
        }

        // 4. Desktop / Apps Catalog
        var desktopNode = AggregateExplorer<DesktopAppState>.PreviewNode<DesktopApp>(new DesktopAppState());
        AssertStructuredMixStyles("Desktop", desktopNode, expectedMinStyles: 2);

        // 5. Creator
        var creatorNode = CreatorView.Render(
            new CreatorWorkspaceState(),
            new BrainTopology([new("local/ctr/default", "ctr", BrainGraph.Unit)], []),
            [],
            [],
            Inspection.None).Root;
        AssertStructuredMixStyles("Creator", creatorNode, expectedMinStyles: 2);

        // 6. Friends
        var friendsNode = AggregateExplorer<FriendsAppState>.PreviewNode<FriendsApp>(new FriendsAppState());
        AssertStructuredMixStyles("Friends", friendsNode, expectedMinStyles: 3);

        // 7. Tasks (board, list, details)
        foreach (var tab in new[] { "board", "list", "details" })
        {
            var tasksNode = AggregateExplorer<TasksWorkspaceState>.PreviewNode<TasksApp>(new TasksWorkspaceState { ActiveTab = tab });
            AssertStructuredMixStyles($"Tasks:{tab}", tasksNode, expectedMinStyles: 4);
        }

        // 8. Data Studio
        var dataNode = AggregateExplorer<DataStudioAppState>.PreviewNode<DataStudioApp>(new DataStudioAppState());
        AssertStructuredMixStyles("DataStudio", dataNode, expectedMinStyles: 4);
    }

    [Fact]
    public async Task FilesAppAndShippedUnitsEmitStructuredMixStyles()
    {
        var storage = new InMemoryStorage("Workspace Disk", kind: "local");
        var projectsDir = storage.AddFolder(storage.Root, "projects");
        var specFile = storage.AddFile(projectsDir, "spec.md", "# Spec\n"u8.ToArray());

        var broker = new SingleStorageBroker("disk", "Workspace Disk", storage);
        var filesApp = new FilesApp(broker, new BrainId("owner"), new FilesState());
        await filesApp.Browse(new FilesLocation("disk", projectsDir), Ct);
        await filesApp.Select(new FilesSelected(new FilesItem("disk", await storage.Stat(specFile, Ct))), Ct);

        var filesScreen = FilesScreen.Compose(filesApp.Snapshot, 1440, previewOpen: true, viewMode: "list");
        var filesWire = AssertStructuredMixStyles("Files", filesScreen.Main, expectedMinStyles: 4);
        Assert.DoesNotContain("\"mono\":true", filesWire, StringComparison.OrdinalIgnoreCase);

        // Verify shipped interactive units author fluent Mix.Style() without legacy string styles
        foreach (var alias in new[] { "helloworld", "fx.rates.monitor", "organizer.work.notes", "triage.github.issues", "watch.x.elonmusk" })
        {
            var files = IntoChat.OS.Apps.ShippedUnits.Files(alias);
            var csSource = string.Join("\n", files.Where(kv => kv.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value));
            Assert.Contains("Mix.Style()", csSource, StringComparison.Ordinal);
            Assert.DoesNotContain("Style = \"", csSource, StringComparison.Ordinal);
        }
    }

    private static string AssertStructuredMixStyles(string appName, Node root, int expectedMinStyles)
    {
        var json = Serialize(root);
        using var doc = JsonDocument.Parse(json);
        AssertNoLegacyButtonStyleStrings(doc.RootElement);

        var styles = CollectStyleObjects(doc.RootElement);
        Assert.True(
            styles.Count >= expectedMinStyles,
            $"Expected at least {expectedMinStyles} structured Mix.Style() objects in {appName}, found {styles.Count}.");

        foreach (var entry in styles)
        {
            Assert.True(
                entry.Style.TryGetProperty("tone", out _) || entry.Style.TryGetProperty("radius", out _) || entry.Style.TryGetProperty("paddingX", out _),
                $"Structured Mix.Style() on {appName} ({entry.Kind}) must emit typed style tokens.");
        }

        return json;
    }

    private static string Serialize(Node node) => JsonSerializer.Serialize(node, RuntimeProtocol.OptionsFor(typeof(Node)));

    private static List<(string Kind, JsonElement Style)> CollectStyleObjects(JsonElement root)
    {
        var results = new List<(string Kind, JsonElement Style)>();
        Visit(root, results);
        return results;

        static void Visit(JsonElement el, List<(string Kind, JsonElement Style)> acc)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("kind", out var kindProp)
                        && kindProp.ValueKind == JsonValueKind.String
                        && el.TryGetProperty("style", out var styleProp)
                        && styleProp.ValueKind == JsonValueKind.Object)
                    {
                        acc.Add((kindProp.GetString()!, styleProp.Clone()));
                    }

                    foreach (var prop in el.EnumerateObject())
                    {
                        Visit(prop.Value, acc);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        Visit(item, acc);
                    }
                    break;
            }
        }
    }

    private static void AssertNoLegacyButtonStyleStrings(JsonElement root)
    {
        Visit(root);

        static void Visit(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("kind", out var kindProp)
                        && kindProp.ValueKind == JsonValueKind.String
                        && kindProp.GetString() is "button" or "iconButton" or "card" or "surface"
                        && el.TryGetProperty("style", out var styleProp))
                    {
                        Assert.NotEqual(JsonValueKind.String, styleProp.ValueKind);
                    }

                    foreach (var prop in el.EnumerateObject())
                    {
                        Visit(prop.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        Visit(item);
                    }
                    break;
            }
        }
    }

    private sealed class SingleStorageBroker(string key, string label, IStorage storage) : ICapabilityBroker
    {
        public Task<TCapability> Resolve<TCapability>(BrainId brain, CancellationToken ct) where TCapability : class
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CapabilityConnection<TCapability>>> ResolveAll<TCapability>(BrainId brain, CancellationToken ct) where TCapability : class
            => Task.FromResult<IReadOnlyList<CapabilityConnection<TCapability>>>(
            [
                new CapabilityConnection<TCapability>(key, label, (TCapability)(object)storage),
            ]);
    }
}
