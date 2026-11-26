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

    [Fact]
    public void ShellOpenFixturesUseStructuredMixStylesMatchingLiveRenderers()
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var appsDir = ResolveAppsViewsDir();
        var allShellApps = new[] { "settings", "gallery", "creator", "ino", "desktop", "data", "markdraw", "photocraft", "filmcraft" };

        foreach (var app in allShellApps)
        {
            var path = Path.Combine(appsDir, app, "open.json");
            Assert.True(File.Exists(path), $"Missing view fixture: {path}");
            using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
            AssertNoLegacyButtonStyleStrings(doc.RootElement.GetProperty("main"));
        }

        void AssertPreviewEqual(string app, Node previewRoot, string title)
        {
            var path = Path.Combine(appsDir, app, "open.json");
            var fixture = JsonSerializer.Deserialize<DigitalBrain.Testing.Views.ViewFixture>(File.ReadAllText(path, System.Text.Encoding.UTF8), jsonOptions)!;
            var expectedJson = JsonSerializer.Serialize(fixture.Main!, jsonOptions);
            var actualJson = JsonSerializer.Serialize(new View(title, previewRoot), jsonOptions);
            Assert.Equal(expectedJson, actualJson);
        }

        AssertPreviewEqual("photocraft", AggregateExplorer<IntoChat.OS.Apps.PhotoCraft.PhotoCraftAppState>.PreviewNode<IntoChat.OS.Apps.PhotoCraft.PhotoCraftApp>(new IntoChat.OS.Apps.PhotoCraft.PhotoCraftAppState()), "PhotoCraft");
        AssertPreviewEqual("filmcraft", AggregateExplorer<IntoChat.OS.Apps.FilmCraft.FilmCraftAppState>.PreviewNode<IntoChat.OS.Apps.FilmCraft.FilmCraftApp>(new IntoChat.OS.Apps.FilmCraft.FilmCraftAppState()), "FilmCraft");
        AssertPreviewEqual(
            "data",
            AggregateExplorer<DataStudioAppState>.PreviewNode<DataStudioApp>(new DataStudioAppState() with
            {
                ChartTitle = "No Data ()",
                ActiveQuery = "SELECT * FROM ",
                Summary = "No dataset found matching query or file reference in database ''.",
                SourceNeuronUri = "neuron://owner/data//empty",
            }),
            "Data Studio");

        void AssertStyleObjectsMatchByKey(string app, Node previewRoot, string title, int minMatched)
        {
            var path = Path.Combine(appsDir, app, "open.json");
            var fixture = JsonSerializer.Deserialize<DigitalBrain.Testing.Views.ViewFixture>(File.ReadAllText(path, System.Text.Encoding.UTF8), jsonOptions)!;
            using var fixDoc = JsonDocument.Parse(JsonSerializer.Serialize(fixture.Main!, jsonOptions));
            using var prevDoc = JsonDocument.Parse(JsonSerializer.Serialize(new View(title, previewRoot), jsonOptions));
            var fixStyles = CollectKeyedStyles(fixDoc.RootElement);
            var prevStyles = CollectKeyedStyles(prevDoc.RootElement);
            var matched = 0;
            foreach (var (k, v) in fixStyles)
            {
                if (prevStyles.TryGetValue(k, out var pv))
                {
                    Assert.Equal(pv, v);
                    matched++;
                }
            }

            Assert.True(matched >= minMatched, $"Expected at least {minMatched} matched styled nodes for {app}, matched {matched}.");
        }

        AssertStyleObjectsMatchByKey("markdraw", AggregateExplorer<IntoChat.OS.Apps.Markdraw.MarkdrawAppState>.PreviewNode<IntoChat.OS.Apps.Markdraw.MarkdrawApp>(new IntoChat.OS.Apps.Markdraw.MarkdrawAppState()), "Markdraw", minMatched: 14);
        AssertStyleObjectsMatchByKey("settings", AggregateExplorer<SettingsAppState>.PreviewNode<SettingsApp>(new SettingsAppState()), "Settings", minMatched: 10);
        AssertStyleObjectsMatchByKey("desktop", AggregateExplorer<DesktopAppState>.PreviewNode<DesktopApp>(new DesktopAppState()), "Desktop", minMatched: 4);
        AssertStyleObjectsMatchByKey(
            "creator",
            CreatorView.Render(
                new CreatorWorkspaceState(),
                new BrainTopology([new("local/ctr/default", "ctr", BrainGraph.Unit)], []),
                [],
                [],
                Inspection.None).Root,
            "Creator",
            minMatched: 8);
    }

    private static Dictionary<string, string> CollectKeyedStyles(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Visit(root);
        return map;

        void Visit(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("kind", out var kProp) && kProp.ValueKind == JsonValueKind.String
                        && el.TryGetProperty("style", out var sProp) && sProp.ValueKind == JsonValueKind.Object)
                    {
                        var kind = kProp.GetString()!;
                        var id = el.TryGetProperty("key", out var keyProp) && keyProp.ValueKind == JsonValueKind.String
                            ? keyProp.GetString()!
                            : el.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String
                                ? tProp.GetString()!
                                : el.TryGetProperty("surfaceKind", out var skProp) && skProp.ValueKind == JsonValueKind.String
                                    ? skProp.GetString()!
                                    : "";
                        if (!string.IsNullOrEmpty(id))
                        {
                            map[$"{kind}:{id}"] = sProp.GetRawText();
                        }
                    }

                    foreach (var p in el.EnumerateObject())
                    {
                        Visit(p.Value);
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

    private static string ResolveAppsViewsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Intochat.OS.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        var root = dir ?? throw new InvalidOperationException("Repo root not found.");
        var slnx = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Intochat.OS.slnx"));
        var appsProjectRel = slnx.Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .First(p => p is not null && p.EndsWith("/IntoChat.OS.Apps.csproj", StringComparison.Ordinal))!;
        var appsProjectDir = Path.GetDirectoryName(Path.Combine(root, appsProjectRel.Replace('/', Path.DirectorySeparatorChar)))!;
        return Path.Combine(appsProjectDir, "views");
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
