using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.ArtCraft;
using IntoChat.OS.Apps.Calculator;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Data;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Apps.Usage;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using Xunit;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

[Collection(nameof(RecordingApp))]
public sealed class ShellAppsScreenBaselineFacts
{
    public static readonly IReadOnlyList<Type> Standalone14Apps =
    [
        typeof(IntoChat.Aspire.Screens.AspireApp),
        typeof(CalculatorApp),
        typeof(CreatorApp),
        typeof(DataStudioApp),
        typeof(DesktopApp),
        typeof(IntoChat.OS.Apps.DevUI.DevUiApp),
        typeof(FilmCraftApp),
        typeof(FriendsApp),
        typeof(GalleryApp),
        typeof(InoApp),
        typeof(MarkdrawApp),
        typeof(IntoChat.OS.Apps.Orleans.OrleansApp),
        typeof(PhotoCraftApp),
        typeof(SettingsApp),
        typeof(TasksApp),
        typeof(UsageApp),
        typeof(IntoChat.OS.Apps.Widgetbook.WidgetbookApp)
    ];

    public static readonly IReadOnlyList<Type> ArtCraftStudio15Apps =
    [
        typeof(ArtCraftApp),
        typeof(VectorCraftApp),
        typeof(LightCraftApp),
        typeof(EffectCraftApp),
        typeof(DesignCraftApp),
        typeof(PdfCraftApp),
        typeof(WordCraftApp),
        typeof(SoundCraftApp),
        typeof(DeckCraftApp),
        typeof(CadCraftApp),
        typeof(GridCraftApp),
        typeof(FigCraftApp),
        typeof(TextureCraftApp),
        typeof(MaterialCraftApp),
        typeof(SolidCraftApp)
    ];

    public static IReadOnlyList<Type> All29ShellApps =>
        [.. Standalone14Apps, .. ArtCraftStudio15Apps];

    private static IReadOnlyList<(string Alias, Type SignalType)> GetScreenSignals(Type screenType)
    {
        var list = new List<(string, Type)>();
        foreach (var method in screenType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<OnAttribute>(true) is null) { continue; }
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && typeof(Signal).IsAssignableFrom(parameters[0].ParameterType))
            {
                var sigType = parameters[0].ParameterType;
                var alias = sigType.GetCustomAttribute<SignalAttribute>(false)?.Alias ?? sigType.Name;
                list.Add((alias, sigType));
            }
        }
        return list;
    }

    [Fact]
    public void All29ShellApps_DiscoveredAndMatchExpectedInventory()
    {
        // The shell apps assembly plus the components whose Screens left it (the Aspire app).
        var concreteScreens = typeof(CalculatorApp).Assembly.GetTypes()
            .Concat(typeof(IntoChat.Aspire.Screens.AspireApp).Assembly.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && IsScreenType(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(35, concreteScreens.Count);
        foreach (var expected in All29ShellApps)
        {
            Assert.Contains(expected, concreteScreens);
        }
    }

    private static bool IsScreenType(Type type)
    {
        var curr = type.BaseType;
        while (curr != null)
        {
            if (curr.IsGenericType && curr.GetGenericTypeDefinition() == typeof(Screen<>))
            {
                return true;
            }
            curr = curr.BaseType;
        }
        return false;
    }

    [Fact]
    public void All29ShellApps_SignalRecords_CanDeserializeFromEmptyJsonWithoutThrowing()
    {
        var testedSignalsCount = 0;
        using var emptyDoc = JsonDocument.Parse("{}");
        var emptyBytes = "{}"u8.ToArray();

        foreach (var appType in All29ShellApps)
        {
            var signals = GetScreenSignals(appType);
            Assert.NotEmpty(signals);

            foreach (var (alias, signalType) in signals)
            {
                testedSignalsCount++;
                var options = RuntimeProtocol.OptionsFor(signalType);

                // Deserialize from byte array
                var instance = JsonSerializer.Deserialize(emptyBytes, signalType, options);
                Assert.NotNull(instance);
                Assert.IsAssignableFrom<Signal>(instance);

                // Deserialize from empty JsonElement
                var fromElement = (Signal?)emptyDoc.RootElement.Deserialize(signalType, options);
                Assert.NotNull(fromElement);
            }
        }

        Assert.True(testedSignalsCount >= 50, $"Expected at least 50 signals tested across 29 shell apps, but found {testedSignalsCount}.");
    }

    [Fact]
    public async Task All29ShellApps_TestOS_RequestEmptyJson_DoesNotThrowOrCrash()
    {
        using var person = Edge.AsPerson();
        using var emptyDoc = JsonDocument.Parse("{}");

        foreach (var appType in All29ShellApps)
        {
            await using var os = TestOS.Local();
            os.Register<DesktopRecordsNeuron>();
            os.Register<TestViewportNeuron>();
            os.Register<TestWorkspaceNeuron>();
            os.RegisterCatalog();
            os.Register(typeof(CalculatorApp).Assembly);
            // The shell apps mount the control neurons of the UI module.
            os.Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
            os.Register(typeof(IntoChat.Aspire.Screens.AspireApp).Assembly);

            var neuronType = appType.GetCustomAttribute<NeuronAttribute>()?.Type ?? appType.Name;
            var screenId = new NeuronId(new BrainId("local"), neuronType, "default");
            var screen = os.Get<IScreen>(screenId);

            var initialView = await screen.Read();
            Assert.NotNull(initialView);

            var signals = GetScreenSignals(appType);
            foreach (var (alias, _) in signals)
            {
                // Request operation with empty JSON {}
                var ex = await Record.ExceptionAsync(() => screen.Request(alias, emptyDoc.RootElement));
                Assert.Null(ex);

                await os.Idle();
                var postView = await screen.Read();
                Assert.NotNull(postView);
            }
        }
    }

    [Fact]
    public async Task ScreenBaseline_TasksApp_PassesAllCases()
    {
        using var person = Edge.AsPerson();
        var cases = ScreenBaseline.Cases(typeof(TasksApp), os =>
        {
            os.RegisterCatalog();
            os.Register(typeof(TasksApp).Assembly);
            // Tasks mounts the control neurons of the UI module.
            os.Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        });

        foreach (var testCase in cases)
        {
            await ScreenBaseline.RunAsync(testCase.Data);
        }
    }

    [Fact]
    public async Task ScreenBaseline_ArtCraftApps_PassesAllCases()
    {
        using var person = Edge.AsPerson();
        var cases = ScreenBaseline.Cases(typeof(ArtCraftApp), os =>
        {
            os.RegisterCatalog();
            os.Register(typeof(ArtCraftApp).Assembly);
        });

        foreach (var testCase in cases)
        {
            await ScreenBaseline.RunAsync(testCase.Data);
        }
    }

    [Fact]
    public async Task DesktopApp_WindowVerbs_ComprehensiveStressTest()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly).Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);

        var desktop = await os.Install<DesktopApp>();
        var desktopScreen = os.Get<IScreen>(new NeuronId(new BrainId("local"), "app.desktop", "default"));

        // 1. Start verb
        await desktop.Request("start", new DesktopStart());
        await os.Idle();
        var v1 = await desktopScreen.Read();
        Assert.NotNull(v1);

        // 2. Layout verb - alternates layout mode
        var initialLayout = desktop.App.State.LayoutMode;
        await desktop.Request("layout", new DesktopLayoutChoice());
        await os.Idle();
        var nextLayout = desktop.App.State.LayoutMode;
        Assert.NotEqual(initialLayout, nextLayout);
        await desktop.Request("layout", new DesktopLayoutChoice());
        await os.Idle();
        Assert.Equal(initialLayout, desktop.App.State.LayoutMode);

        // 3. Open verb - opens calculator and creator
        await desktop.Request("open", new DesktopOpen("calculator"));
        await os.Idle();
        Assert.Contains(desktop.App.State.Windows, w => w.Tabs.Any(t => t.Id == "calculator"));

        await desktop.Request("open", new DesktopOpen("creator", "3d"));
        await os.Idle();
        Assert.Contains(desktop.App.State.Windows, w => w.Tabs.Any(t => t.Id == "creator"));

        // 4. Focus verb
        var calcWin = desktop.App.State.Windows.First(w => w.Tabs.Any(t => t.Id == "calculator"));
        await desktop.Request("focus", new DesktopFocus(calcWin.Id));
        await os.Idle();
        Assert.Equal("calculator", desktop.App.State.FocusedViewId);

        // 5. NewTab verb
        await desktop.Request("newtab", new DesktopNewTab(calcWin.Id));
        await os.Idle();
        var updatedCalcWin = desktop.App.State.Windows.First(w => w.Id == calcWin.Id);
        Assert.True(updatedCalcWin.Tabs.Length >= 2);

        // 6. Tab verb - switch active tab
        await desktop.Request("tab", new DesktopTab($"{calcWin.Id}/0"));
        await os.Idle();
        var tabSwitchedWin = desktop.App.State.Windows.First(w => w.Id == calcWin.Id);
        Assert.Equal(tabSwitchedWin.Tabs[0].Id, tabSwitchedWin.ActiveTabId);

        // 7. Pin & Unpin verbs
        await desktop.Request("pin", new DesktopPinApp("tasks", Pinned: true));
        await os.Idle();
        Assert.Contains("tasks", desktop.App.State.PinnedApps, StringComparer.OrdinalIgnoreCase);

        await desktop.Request("unpin", new DesktopUnpinApp("tasks"));
        await os.Idle();
        Assert.DoesNotContain("tasks", desktop.App.State.PinnedApps, StringComparer.OrdinalIgnoreCase);

        // 8. Favourite verb
        await desktop.Request("favourite", new DesktopFavourite(calcWin.Id));
        await os.Idle();
        Assert.NotEmpty(desktop.App.State.Favourites);

        await desktop.Request("favourite", new DesktopFavourite(calcWin.Id));
        await os.Idle();
        Assert.Empty(desktop.App.State.Favourites);

        // 9. Widget Open & Close verbs
        await desktop.Request("widget-open", new DesktopWidgetOpen("telemetry-radar"));
        await os.Idle();
        var recs = await os.Get<IDesktopRecords>(new NeuronId(new BrainId("local"), "apps.desktop-records", IDesktopRecords.Key)).Read();
        Assert.Contains("telemetry-radar", recs.Widgets);

        await desktop.Request("widget-close", new DesktopWidgetClose("telemetry-radar"));
        await os.Idle();
        recs = await os.Get<IDesktopRecords>(new NeuronId(new BrainId("local"), "apps.desktop-records", IDesktopRecords.Key)).Read();
        Assert.DoesNotContain("telemetry-radar", recs.Widgets);

        // 10. Narrow & Auto-arrange verbs
        await desktop.Request("narrow", new DesktopNarrow("true"));
        await os.Idle();
        Assert.True(desktop.App.State.IsNarrow);

        await desktop.Request("auto-arrange", new DesktopAutoArrange());
        await os.Idle();
        Assert.Equal("whiteboard-auto-arranged", desktop.App.State.LayoutMode);

        // 11. Close verb
        var winToClose = desktop.App.State.Windows.First(w => w.Tabs.Any(t => t.Id == "creator"));
        await desktop.Request("close", new DesktopClose(winToClose.Id));
        await os.Idle();
        Assert.DoesNotContain(desktop.App.State.Windows, w => w.Id == winToClose.Id);

        // 12. Close-App verb
        await desktop.Request("close-app", new DesktopCloseApp("calculator"));
        await os.Idle();
        Assert.DoesNotContain(desktop.App.State.Windows, w => w.Tabs.Any(t => t.Id.StartsWith("calculator", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DesktopApp_WindowClose_ResetsViewportToNull_EmpiricalVerification()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly).Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);

        var desktop = await os.Install<DesktopApp>();

        // Open calculator window
        await desktop.Request("open", new DesktopOpen("calculator"));
        await os.Idle();

        var calcWin = desktop.App.State.Windows.First(w => w.Tabs.Any(t => t.Id == "calculator"));
        var winId = calcWin.Id;
        var vpId = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId("local"), DesktopApp.AppName).ChildNeuron(UIVocabulary.ViewportType, $"window/{winId}");

        // Manually assign a non-null target to the viewport of window winId
        var viewport = os.Get<IViewport>(vpId);
        var initialVpState = await viewport.Read();
        var sampleRef = new WorkspaceViewRef("calculator-view", "apps/calculator", "full", "");
        await viewport.Set(sampleRef, initialVpState.Revision);

        var vpAfterSet = await viewport.Read();
        Assert.NotNull(vpAfterSet.Target);
        Assert.Equal("calculator-view", vpAfterSet.Target.Id);

        // Close window winId via desktop "close"
        await desktop.Request("close", new DesktopClose(winId));
        await os.Idle();

        // Empirically verify that ClearViewportsAsync set the viewport target to null!
        var vpAfterClose = await viewport.Read();
        Assert.Null(vpAfterClose.Target);
    }

    [Fact]
    public async Task DesktopApp_CloseApp_ResetsViewportToNull_EmpiricalVerification()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly).Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);

        var desktop = await os.Install<DesktopApp>();

        // Open tasks window
        await desktop.Request("open", new DesktopOpen("tasks"));
        await os.Idle();

        var tasksWin = desktop.App.State.Windows.First(w => w.Tabs.Any(t => t.Id == "tasks"));
        var winId = tasksWin.Id;
        var vpId = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId("local"), DesktopApp.AppName).ChildNeuron(UIVocabulary.ViewportType, $"window/{winId}");

        // Assign a non-null target
        var viewport = os.Get<IViewport>(vpId);
        var initialVpState = await viewport.Read();
        var sampleRef = new WorkspaceViewRef("tasks-view", "apps/tasks", "full", "");
        await viewport.Set(sampleRef, initialVpState.Revision);

        Assert.NotNull((await viewport.Read()).Target);

        // Close app via "close-app"
        await desktop.Request("close-app", new DesktopCloseApp("tasks"));
        await os.Idle();

        // Empirically verify that the viewport target was reset to null!
        var vpAfterClose = await viewport.Read();
        Assert.Null(vpAfterClose.Target);
    }
}

// In-process test doubles for Desktop integration testing
[Neuron("viewport")]
public sealed class TestViewportNeuron : Neuron<TestViewportState>, IViewport
{
    public Task<ViewportState> Read() =>
        Task.FromResult(new ViewportState(Id.Key.ToString(), State.Revision, State.Target));

    public async Task Set(WorkspaceViewRef? target, long expectedRevision)
    {
        var next = State with { Revision = State.Revision + 1, Target = target };
        await Save(next, new ViewportChanged(Id.Key.ToString(), next.Revision));
    }
}

public sealed record TestViewportState(long Revision = 1, WorkspaceViewRef? Target = null)
{
    public TestViewportState() : this(1, null) { }
}

[Neuron("ui.workspace")]
public sealed class TestWorkspaceNeuron : Neuron<TestWorkspaceStorage>, IWorkspace
{
    public Task<WorkspaceState> Read() => Task.FromResult(new WorkspaceState(
        State.Revision,
        State.Path,
        State.Open)
    {
        OpenViews = State.OpenViews,
        FocusedViewId = State.FocusedViewId,
        Pins = State.Pins,
        Windows = State.Windows,
        Favourites = State.Favourites,
    });

    public async Task<WorkspaceState> Apply(WorkspaceUpdate next, string operationId, long expectedRevision)
    {
        var nextWindows = next.Windows ?? State.Windows;
        var nextViews = next.OpenViews ?? State.OpenViews;
        var nextPins = next.Pins ?? State.Pins;
        var nextFavs = next.Favourites ?? State.Favourites;
        var nextFocus = next.FocusedViewId ?? State.FocusedViewId;

        var nextState = State with
        {
            Revision = State.Revision + 1,
            Windows = nextWindows,
            OpenViews = nextViews,
            Pins = nextPins,
            Favourites = nextFavs,
            FocusedViewId = nextFocus
        };
        await Save(nextState, new IntoChat.OS.Modules.Flutter.Workspace.Signals.WorkspaceChanged(Id.Key.ToString(), nextState.Revision));
        return await Read();
    }

    public Task<WorkspaceState> Show(string app, string operationId, long expectedRevision) => Read();
    public Task<WorkspaceState> Back(string operationId, long expectedRevision) => Read();
    public Task<WorkspaceState> Home(string operationId, long expectedRevision) => Read();
    public Task<WorkspaceState> Close(string app, string operationId, long expectedRevision) => Read();
}

public sealed record TestWorkspaceStorage(
    long Revision = 1,
    string[]? Path = null,
    string[]? Open = null,
    WorkspaceViewRef[]? Pins = null,
    WorkspaceViewRef[]? OpenViews = null,
    string? FocusedViewId = null,
    WorkspaceWindow[]? Windows = null,
    WorkspaceViewRef[]? Favourites = null)
{
    public TestWorkspaceStorage() : this(1, null, null, null, null, null, null, null) { }

    public string[] Path { get; init; } = Path ?? [];
    public string[] Open { get; init; } = Open ?? [];
    public WorkspaceViewRef[] Pins { get; init; } = Pins ?? [];
    public WorkspaceViewRef[] OpenViews { get; init; } = OpenViews ?? [];
    public WorkspaceWindow[] Windows { get; init; } = Windows ?? [];
    public WorkspaceViewRef[] Favourites { get; init; } = Favourites ?? [];
}
