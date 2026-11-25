using System.Reflection;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

[Collection(nameof(RecordingApp))]
public sealed class DesktopAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> DesktopOperations => ScreenBaseline.Cases(typeof(DesktopApp), os => os.Register<DesktopRecordsNeuron>().Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly));

    [Theory, MemberData(nameof(DesktopOperations))]
    public async Task DesktopBaseline(ScreenOperationCase operation)
    {
        using var person = Edge.AsPerson();
        await ScreenBaseline.RunAsync(operation);
    }

    [Fact]
    public async Task DesktopScenariosPass()
    {
        using var person = Edge.AsPerson("test");
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly).Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.desktop", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public void DesktopCarriesTheFlutterTree()
    {
        Assert.Equal("desktop", DesktopApp.Bundle.Name);
    }

    [Fact]
    public async Task OpenWithAppAndViewRoutesToThatView()
    {
        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        os.Register<RecordingApp>();
        var desktop = await os.Install<DesktopApp>();

        await desktop.Request("open", new DesktopOpen("creator", "trace/t-1"));

        Assert.Equal(("apps/creator", "open", "trace/t-1"), Assert.Single(RecordingApp.Requests));
    }

    [Fact]
    public async Task LegacyOpenByNameStillOpensTheWindow()
    {
        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        var desktop = await os.Install<DesktopApp>();

        // "open:tasks" binds the name alone; no app neuron is registered, so a forward would throw.
        await desktop.Request("open", new DesktopOpen("tasks"));
        await desktop.Request("open", new DesktopOpen("creator", "trace/t-1"));

        Assert.Empty(RecordingApp.Requests);
        Assert.Null(desktop.App.RecordedError);
    }

    [Fact]
    public async Task CatalogAndShippedManifestsCarryArtCraftIconsAndCalculator()
    {
        var view = ScreenPreview.With<DesktopApp, View>(app => app.Render());
        var tiles = new PresentedView("main", view).Nodes
            .OfType<AppTileNode>()
            .Where(t => t.Key.StartsWith("catalog-", StringComparison.Ordinal))
            .ToArray();

        foreach (var studio in IntoChat.OS.Modules.ArtCraft.ArtCraftCatalog.All)
        {
            var entry = Assert.Single(tiles, c => c.AppId == studio.Id);
            Assert.Equal($"artcraft:{studio.Id}", entry.Icon);
            Assert.Equal(AppLaunch.Maximized, entry.Launch);
        }

        Assert.Contains(tiles, c => c.AppId == "calculator" && c.Icon == "calculator");
        Assert.Contains(tiles, c => c.AppId == "photocraft" && c.Icon == "artcraft:photocraft");
        Assert.Contains(tiles, c => c.AppId == "filmcraft" && c.Icon == "artcraft:filmcraft");
        Assert.Contains(tiles, c => c.AppId == "markdraw");
        Assert.Contains(tiles, c => c.AppId == "creator");
        Assert.Contains(tiles, c => c.AppId == "settings");

        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly, typeof(DesktopApp).Assembly]);
        var manifests = await ShippedManifests.Of(catalog);
        Assert.Equal("artcraft:photocraft", manifests.IconKey("photocraft"));
        Assert.Equal("artcraft:filmcraft", manifests.IconKey("filmcraft"));
        Assert.Equal("artcraft:vectorcraft", manifests.IconKey("vectorcraft"));
        Assert.Equal("artcraft:solidcraft", manifests.IconKey("solidcraft"));
        Assert.Equal("capability:calculator", manifests.IconKey("calculator"));
    }

    [Fact]
    public async Task DesktopDynamicallyShowsDeployedRuntimeUnitsAndOpensAndPinsThem()
    {
        FakeDeployedUnitsStudio.Aliases = ["helloworld", "btcalert"];
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<FakeDeployedUnitsStudio>();
        var desktop = await os.Install<DesktopApp>();

        await desktop.Request("start", new DesktopStart());

        Assert.NotNull(desktop.App.State.DeployedUnits);
        Assert.Equal(["btcalert", "helloworld"], desktop.App.State.DeployedUnits!);

        var view = desktop.App.Render();
        var tiles = new PresentedView("main", view).Nodes.OfType<AppTileNode>().ToArray();
        Assert.Contains(tiles, item => item.Key == "catalog-helloworld" && item.AppId == "helloworld" && item.AppKind == "unit");
        Assert.Contains(tiles, item => item.Key == "catalog-btcalert" && item.AppId == "btcalert" && item.AppKind == "unit");

        var viewJson = System.Text.Json.JsonSerializer.Serialize(
            view,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("dock-photocraft", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-filmcraft", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-markdraw", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-creator", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-unit-helloworld", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-unit-btcalert", viewJson, StringComparison.Ordinal);

        await desktop.Request("pin", new DesktopPinApp("helloworld", Pinned: true));
        Assert.Contains("helloworld", desktop.App.State.PinnedApps, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DesktopApp_LogsSwallowedFallbackToConstructorInjectedLogger()
    {
        var logger = new RecordingLogger<DesktopApp>();
        var services = new ServiceCollection()
            .AddSingleton<ILogger<DesktopApp>>(logger)
            .BuildServiceProvider();
        var app = ActivatorUtilities.CreateInstance<DesktopApp>(services);

        var view = app.Render();

        Assert.NotNull(view);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Id.Brain is unassigned in standalone tests", StringComparison.Ordinal));
    }

    [Fact]
    public void DesktopAppState_AggregateExplorer_UnfoldsHealthyStateGraphAndRuleGuards()
    {
        var instanceFields = typeof(DesktopApp)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .ToArray();
        Assert.Empty(instanceFields);

        var graph = AggregateExplorer<DesktopAppState>.ExploreScreen<DesktopApp>(maxStates: 48).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.Empty(graph.Violations);
        Assert.Contains(graph.States, s => s.State.LayoutMode == "smart-desktop");
        Assert.Contains(graph.States, s => s.State.LayoutMode == "spatial-grid");
        Assert.Contains(graph.States, s => s.State.LayoutMode == "whiteboard-auto-arranged");
        Assert.Contains(graph.States, s => s.State.Windows.Any(w => w.Id == "w-1"));
        Assert.Contains(graph.States, s => s.State.PinnedApps.Contains("photocraft", StringComparer.OrdinalIgnoreCase));
        Assert.Contains(graph.States, s => s.State.WidgetsText == "clock");

        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_layout", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_app", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("window_not_found", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("tab_not_found", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("core_dock_app", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_widget", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("widget_not_open", StringComparer.Ordinal));

        Assert.Contains("digraph DesktopAppState", graph.ToDot(), StringComparison.Ordinal);
        Assert.Contains(DesktopApp.Bundle.Manifest.Operations, op => op.Name == UiCommand.SignalAlias);
    }

    [Fact]
    public async Task DesktopApp_DomainRules_AndUiIntrospector_UnfoldCleanAccordantStateGraphAndDot()
    {
        var state = new DesktopAppState();
        string[] eightApps = ["ino", "calculator", "creator", "tasks", "gallery", "markdraw", "friends", "settings"];
        foreach (var app in eightApps)
        {
            var opened = state.OpenWindow(app);
            Assert.True(opened.IsAccepted);
            state = opened.Next;
        }

        var ninthWin = state.OpenWindow("photocraft");
        Assert.True(ninthWin.IsRefused);
        Assert.Contains("max_windows", ninthWin.Rejection!.Rules);

        var tabState = new DesktopAppState().OpenWindow("calculator").Next!;
        for (var i = 0; i < 7; i++)
        {
            var added = tabState.AddWindowTab("w-1", "ino");
            Assert.True(added.IsAccepted);
            tabState = added.Next!;
        }

        var ninthTab = tabState.AddWindowTab("w-1", "ino");
        Assert.True(ninthTab.IsRefused);
        Assert.Contains("max_tabs_per_window", ninthTab.Rejection!.Rules);

        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(UiModule).Assembly);
        os.Register(typeof(DesktopApp).Assembly).Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("open", new DesktopOpen("desktop"));
        Assert.Contains("invalid_app", desktop.App.LastRejection!.Rules);

        await desktop.Request("focus", new DesktopFocus("w-missing"));
        Assert.Contains("window_not_found", desktop.App.LastRejection!.Rules);

        await desktop.Request("tab", new DesktopTab("w-missing/0"));
        Assert.Contains("tab_not_found", desktop.App.LastRejection!.Rules);

        await desktop.Request("unpin", new DesktopUnpinApp("ino"));
        Assert.Contains("core_dock_app", desktop.App.LastRejection!.Rules);

        await desktop.Request("widget-close", new DesktopWidgetClose("missing"));
        Assert.Contains("widget_not_open", desktop.App.LastRejection!.Rules);

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(new BrainId("local")));
        var snapshot = await introspector.ExploreStateGraph(typeof(DesktopApp).FullName!, maxStates: 48);

        Assert.True(snapshot.IsHealthy, string.Join("\n", snapshot.Violations));
        Assert.Empty(snapshot.Violations);
        Assert.True(snapshot.States.Length >= 6);
        Assert.Contains(snapshot.Transitions, t => t.Accepted);
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("core_dock_app", StringComparer.Ordinal));
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("window_not_found", StringComparer.Ordinal));
        Assert.Contains("digraph DesktopAppState", snapshot.Dot, StringComparison.Ordinal);

        var report = await introspector.SyncContext();
        Assert.Contains("DesktopApp", report.ContextSummary, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [DigitalBrain.Neurons.Neuron("ino.synapses")]
    private sealed class FakeDeployedUnitsStudio : DigitalBrain.Neurons.Neuron, ISynapseStudio
    {
        public static string[] Aliases { get; set; } = [];

        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
            => throw new NotSupportedException();
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult("ok"));
        public Task<DigitalBrain.Units.DeployedSource[]> List() => Task.FromResult(
            Aliases.Select(a => new DigitalBrain.Units.DeployedSource(a, "hash", [a], DigitalBrain.Units.DeployStatus.Deployed, null, DateTimeOffset.UtcNow, null)).ToArray());
        public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult("none"));
    }
}
