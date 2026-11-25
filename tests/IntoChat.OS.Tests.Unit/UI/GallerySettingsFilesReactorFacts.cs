/*
# Gallery, Settings & Files DDD Reactor & AggregateExplorer Verification Suite
Role: Default
Shipped: false

Comprehensive verification for Wave 2 Flagships #2, #3 & Kit Gallery:
- `UK-9` (`INT-1357`): `GalleryApp` (`AggregateScreen<GalleryAppState>`) + live `BankAccount` DDD Aggregate + `StateGraphView`
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
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;

namespace IntoChat.OS.Tests.Unit.UI;

public sealed class GallerySettingsFilesReactorFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BankAccountAggregateExplorerUnfoldsHealthyStateGraphAndSufficientFundsSelfLoops()
    {
        var choices = new ChoiceSet()
            .ForParameter("holder", "alice")
            .ForParameter("amount", 50m, 30m, 70m);

        var graph = AggregateExplorer<BankAccount>.Explore(
            choices: choices,
            maxStates: 16).EnsureHealthy();

        Assert.True(graph.IsHealthy);
        Assert.True(graph.States.Count >= 3);
        Assert.Contains(graph.AcceptedTransitions, t => t.Command.StartsWith("Open(", StringComparison.Ordinal));
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "Deposit(50)");
        Assert.Contains(graph.AcceptedTransitions, t => t.Command == "Withdraw(30)");
        Assert.Contains(
            graph.RejectedTransitions,
            t => t.Command == "Withdraw(70)" && t.BrokenRules.Contains("sufficient_funds", StringComparer.Ordinal));

        var scene = Kit.StateGraphView(graph);
        Assert.Equal(graph.States.Count, scene.Nodes.Count);
        Assert.NotEmpty(scene.Synapses);
    }

    [Fact]
    public void GalleryAppStateExplorerVerifiesAllTabsUniqueKeysAndZeroMonospace()
    {
        var choices = new ChoiceSet()
            .ForParameter("page", "home", "colors", "tabs", "charts", "graphs")
            .ForParameter("holder", "alice")
            .ForParameter("amount", 50m, 30m, 70m)
            .ForParameter("stateId", "s0")
            .ForParameter("label", "Primary");
        var graph = AggregateExplorer<GalleryAppState>.ExploreScreen<GalleryApp>(
            choices: choices,
            maxStates: 40).EnsureHealthy();

        Assert.True(graph.IsHealthy);
        Assert.Contains(graph.States, s => s.State.Page == "home");
        Assert.Contains(graph.States, s => s.State.Page == "colors");
        Assert.Contains(graph.States, s => s.State.Page == "charts");
        Assert.Contains(graph.States, s => s.State.Page == "graphs");
        Assert.Contains(
            graph.RejectedTransitions,
            t => t.Command == "WithdrawFunds(70)" && t.BrokenRules.Contains("sufficient_funds", StringComparer.Ordinal));

        foreach (var page in GalleryAppState.PageIds)
        {
            var node = AggregateExplorer<GalleryAppState>.PreviewNode<GalleryApp>(new GalleryAppState().SelectPage(page));
            var wire = JsonSerializer.Serialize(node, RuntimeProtocol.OptionsFor(typeof(Node)));
            Assert.DoesNotContain("\"mono\":true", wire, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GalleryAppShowcasesWinUi3PagesLiveModulesUiNeuronsAndBankGuards()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("gallery-test", "gallery-test", new BrainId("gallery-test")));
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        os.Register(typeof(GalleryApp).Assembly);

        var brain = new BrainId("gallery-test");
        var id = new NeuronId(brain, "app.gallery", "default");
        var screen = os.Get<IScreen>(id);

        // 1. Home page: AppShell root with WinUI 3 navigation rail & live identity switcher
        var opened = await screen.Open();
        Assert.NotNull(opened);
        var homeWire = opened!.View.GetRawText();
        Assert.Contains("\"kind\":\"appShell\"", homeWire, StringComparison.Ordinal);
        Assert.Contains("Welcome to IntoChat UI Gallery", homeWire, StringComparison.Ordinal);

        // 2. Charts page: two independent live IChart neurons (stream + query-bound revenue)
        await screen.Click("nav:charts");
        await os.Idle();
        var chartsView = await screen.Open();
        var chartsWire = chartsView!.View.GetRawText();
        Assert.Contains("\"refKind\":\"ui.chart\"", chartsWire, StringComparison.Ordinal);
        Assert.Contains(GalleryApp.StreamChartKey, chartsWire, StringComparison.Ordinal);
        Assert.Contains(GalleryApp.RevenueChartKey, chartsWire, StringComparison.Ordinal);

        // 3. Graphs & DDD Rules page: open BankAccount for Alice -> +$50 -> -$70 refused -> -$30 succeeds
        await screen.Click("nav:graphs");
        await os.Idle();
        var graphsInitial = await screen.Open();
        Assert.NotNull(graphsInitial);

        await screen.Click("bank:open");
        await os.Idle();
        var afterOpen = await screen.Open();
        Assert.NotNull(afterOpen);

        await screen.Click("bank:deposit-50");
        await os.Idle();
        var afterDeposit = await screen.Open();
        Assert.NotNull(afterDeposit);
        var depositWire = afterDeposit!.View.GetRawText();
        Assert.Contains("Alice: $50", depositWire, StringComparison.Ordinal);
        Assert.Contains("Insufficient funds: balance $50, requested $70.", depositWire, StringComparison.Ordinal);

        var refuse70 = JsonSerializer.SerializeToElement(
            new UiCommand(Target: "state", Method: "WithdrawFunds", Args: [JsonSerializer.SerializeToElement(70m)]),
            RuntimeProtocol.OptionsFor(typeof(UiCommand)));
        await screen.Request(UiCommand.SignalAlias, refuse70);
        await os.Idle();
        var afterRefused = await screen.Open();
        Assert.Contains("Domain rule refused command", afterRefused!.View.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Alice: $50", afterRefused.View.GetRawText(), StringComparison.Ordinal);

        await screen.Click("bank:withdraw-30");
        await os.Idle();
        var afterWithdraw = await screen.Open();
        var withdrawWire = afterWithdraw!.View.GetRawText();
        Assert.Contains("Alice: $20", withdrawWire, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"brainScene\"", withdrawWire, StringComparison.Ordinal);
        Assert.Contains("sufficient_funds", withdrawWire, StringComparison.Ordinal);
    }

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
