/*
# Settings & Files DDD Reactor & AggregateExplorer Verification Suite
Role: Default
Shipped: false

Comprehensive verification for Wave 2 Flagships #2 & #3:
- `UK-16` (`INT-1364`): `SettingsApp` (`AggregateScreen<SettingsAppState>`) + zero mutable grain fields + all 13 sections + `Rule` budget guard
- `UK-17` (`INT-1365`): `FilesScreen` / `FilesUi` / `FilesWorkspaceView` on `IntoChat.OS.UI.Kit` + Finder layout + `Rule` path validation
*/
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;

namespace IntoChat.OS.Tests.Unit.UI;

public sealed class SettingsFilesReactorFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void SettingsAppHasZeroMutableInstanceFieldsAndSettingsAppStatePassesExplorer()
    {
        // Verify Orleans Grain anti-pattern fix: zero private mutable fields on SettingsApp!
        var instanceFields = typeof(SettingsApp)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .ToArray();
        Assert.Empty(instanceFields);

        // Verify OverrideBody was removed from SettingsAppState!
        Assert.Null(typeof(SettingsAppState).GetProperty("OverrideBody"));

        var choices = new ChoiceSet()
            .ForParameter("section", "general", "models", "billing", "apps:ino")
            .ForParameter("subPage", null, "local-server")
            .ForParameter("sheet", (string?)null)
            .ForParameter("searchQuery", (string?)null)
            .ForParameter("query", "", "Ollama")
            .ForParameter("monthlyBudgetMicros", 50_000_000L, 0L);

        var graph = AggregateExplorer<SettingsAppState>.ExploreScreen<SettingsApp>(
            choices: choices,
            maxStates: 48).EnsureHealthy();

        Assert.True(graph.IsHealthy);
        Assert.Contains(
            graph.RejectedTransitions,
            t => t.Command == "ApplyBudgetCap(0)" && t.BrokenRules.Contains("valid_monthly_budget", StringComparer.Ordinal));

        // Verify all 13 sections render cleanly without duplicate keys or monospace
        foreach (var entry in SettingsApp.SearchIndex)
        {
            var state = new SettingsAppState().Navigate(entry.SectionId);
            var node = AggregateExplorer<SettingsAppState>.PreviewNode<SettingsApp>(state);
            var wire = JsonSerializer.Serialize(node, RuntimeProtocol.OptionsFor(typeof(Node)));
            Assert.Contains(entry.Title.Replace("&", "\\u0026", StringComparison.Ordinal), wire, StringComparison.Ordinal);
            Assert.DoesNotContain("\"mono\":true", wire, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task FilesScreenRendersFinderLayoutListAndGridModesEmptyStateAndEnforcesPathRules()
    {
        // 1. Verify Rule-guarded path and inline-size validation
        Assert.False(FilesPathRules.ValidateSingleSegmentName("report-2026.md").Broken);
        Assert.True(FilesPathRules.ValidateSingleSegmentName("").Broken);
        Assert.True(FilesPathRules.ValidateSingleSegmentName("../secret.txt").Broken);
        Assert.True(FilesPathRules.ValidateSingleSegmentName("nested/file.txt").Broken);
        Assert.False(FilesPathRules.ValidateInlineTextSize("short note").Broken);
        Assert.True(FilesPathRules.ValidateInlineTextSize(new string('x', (256 * 1024) + 8)).Broken);

        // 2. Verify empty folder renders EmptyStateNode with zero duplicate keys
        var storage = new InMemoryStorage("Workspace Disk", kind: "local");
        var emptyDir = storage.AddFolder(storage.Root, "empty-docs");
        var projectsDir = storage.AddFolder(storage.Root, "projects");
        var specFile = storage.AddFile(projectsDir, "architecture.md", "# Warm Ink & Paper\n"u8.ToArray());

        var broker = new SingleStorageBroker("disk", "Workspace Disk", storage);
        var app = new FilesApp(broker, new BrainId("owner"), new FilesState());

        await app.Browse(new FilesLocation("disk", emptyDir), Ct);
        var emptyScreen = FilesScreen.Compose(app.Snapshot, 1440, previewOpen: false);
        var emptyWire = JsonSerializer.Serialize(emptyScreen.Main, RuntimeProtocol.OptionsFor(typeof(Node)));
        Assert.Contains("\"kind\":\"emptyState\"", emptyWire, StringComparison.Ordinal);
        Assert.Contains(FilesCopy.EmptyHeading, emptyWire, StringComparison.Ordinal);
        Assert.Contains(FilesCopy.EmptyBody, emptyWire, StringComparison.Ordinal);

        // 3. Verify populated folder with selected file renders ResponsiveSplit (splitPane), Table (list mode), Grid (grid mode), and QuickView metadata
        await app.Browse(new FilesLocation("disk", projectsDir), Ct);
        await app.Select(new FilesSelected(new FilesItem("disk", await storage.Stat(specFile, Ct))), Ct);

        var listScreen = FilesScreen.Compose(app.Snapshot, 1440, previewOpen: true, viewMode: "list");
        var listWire = JsonSerializer.Serialize(listScreen.Main, RuntimeProtocol.OptionsFor(typeof(Node)));
        Assert.Contains("\"kind\":\"splitPane\"", listWire, StringComparison.Ordinal);
        Assert.Contains("\"key\":\"item-open-0\"", listWire, StringComparison.Ordinal);
        Assert.DoesNotContain("Folder contents", listWire, StringComparison.Ordinal);
        Assert.Contains(FilesCopy.QuickViewHeading, listWire, StringComparison.Ordinal);
        Assert.DoesNotContain("\"mono\":true", listWire, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            "Windows Storage",
            FilesScreen.FormatStorageLabel(new FilesStorage("c1", "5584b766030a4193913bc55bf278b487", "windows", ItemFlags.None, null)));

        var gridScreen = FilesScreen.Compose(app.Snapshot, 1440, previewOpen: true, viewMode: "grid");
        var gridWire = JsonSerializer.Serialize(gridScreen.Main, RuntimeProtocol.OptionsFor(typeof(Node)));
        Assert.Contains("\"kind\":\"grid\"", gridWire, StringComparison.Ordinal);
        Assert.Contains("Grid view", gridWire, StringComparison.Ordinal);
        Assert.DoesNotContain("\"mono\":true", gridWire, StringComparison.OrdinalIgnoreCase);
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

