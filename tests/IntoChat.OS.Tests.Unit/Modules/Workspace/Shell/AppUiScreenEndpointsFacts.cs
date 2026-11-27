using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Errors;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.AspNetCore.Http;
using Xunit;
using IntoChat.OS.Host.Kernel;

namespace IntoChat.OS.Modules.Flutter;

public sealed class AppUiScreenEndpointsFacts
{
    private const string Scope = "owner";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PublishedView CreateTestView(string alias, long generation, string key, string emitSignal)
    {
        var view = new View(
            "TestApp",
            new Column([
                new Button(key, "ClickMe", new Command(".", emitSignal, null)),
                new Conversation("btn-alien-thread", [], new ComposerNode("btn-alien", new Command("alien", "test.alien", null)))
            ]));
        var json = JsonSerializer.SerializeToElement(view, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));
        return new PublishedView(alias, generation, json);
    }

    private sealed class FakeScreen : IScreen
    {
        private readonly PublishedView _published;

        public FakeScreen(PublishedView published)
        {
            _published = published;
        }

        public Task<PublishedView?> Open() => Read();

        public Task<PublishedView?> Read(string? alias = null)
            => Task.FromResult<PublishedView?>(_published);

        public Task Request(string signal, JsonElement? args = null) => Task.CompletedTask;
        public Task<PublishedView?> ReadChild(string path) => Task.FromException<PublishedView?>(new ActivationRefusedException(ActivationRefusedException.UnknownChild));
        public Task Dispatch(DigitalBrain.UI.Command command) => Task.CompletedTask;
    }

    private sealed class ScreenBrain : INeuronProxies
    {
        private readonly Dictionary<string, IScreen> _screens = new();

        public void RegisterScreen(string type, IScreen screen)
        {
            _screens[type] = screen;
        }

        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IScreen))
            {
                if (_screens.TryGetValue(id.Type, out var screen))
                {
                    return (T)screen;
                }
                throw new InvalidOperationException($"No screen registered for {id.Type}");
            }
            if (typeof(T) == typeof(IViewport))
            {
                return (T)(object)new FakeViewport();
            }
            if (typeof(T) == typeof(IDesktopShell) && _desktopShell is not null)
            {
                return (T)(object)_desktopShell;
            }
            throw new InvalidOperationException($"No {typeof(T).Name} in this mock brain.");
        }

        private IDesktopShell? _desktopShell;
        public void RegisterDesktopShell(IDesktopShell shell) => _desktopShell = shell;
    }

    private sealed class FakeDesktopShell(DesktopShellProjection projection) : IDesktopShell
    {
        public Task<DesktopShellProjection> ReadProjection() => Task.FromResult(projection);
    }

    private sealed class FakeViewport : IViewport
    {
        public Task<ViewportState> Read()
            => Task.FromResult(new ViewportState("owner/apps/calculator", 1L, new WorkspaceViewRef("v1", "owner/apps/calculator", "main", "default")));
        public Task Set(WorkspaceViewRef? target, long expectedRevision)
        {
            _ = target;
            _ = expectedRevision;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Viewport_Reading_For_Screen_Returns_Direct_ViewRef()
    {
        var brain = new ScreenBrain();
        var published = CreateTestView("app.calculator", 1L, "btn-0", "calc.press");
        var fakeScreen = new FakeScreen(published);
        brain.RegisterScreen("app.calculator", fakeScreen);

        var result = await AppUiEndpoints.ReadNode("viewport", "owner/apps/calculator", brain, Scope, Ct);
        Assert.NotNull(result);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var json = JsonSerializer.Serialize(valueResult.Value, JsonSerializerOptions.Web);
        Assert.Contains("\"root\":{\"kind\":\"view\",\"name\":\"owner/apps/calculator/view\"}", json);
    }

    [Fact]
    public async Task View_Reading_For_Screen_Returns_PublishedView()
    {
        var brain = new ScreenBrain();
        var published = CreateTestView("app.calculator", 1L, "btn-0", "calc.press");
        var fakeScreen = new FakeScreen(published);
        brain.RegisterScreen("app.calculator", fakeScreen);

        var result = await AppUiEndpoints.ReadNode("view", "owner/apps/calculator/view", brain, Scope, Ct);
        Assert.NotNull(result);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var json = JsonSerializer.Serialize(valueResult.Value, JsonSerializerOptions.Web);
        Assert.Contains("\"alias\":\"app.calculator\"", json);
        Assert.Contains("\"generation\":1", json);
    }

    [Fact]
    public async Task View_Reading_For_Foreign_Brain_Throws_UnauthorizedAccessException()
    {
        var brain = new ScreenBrain();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AppUiEndpoints.ReadNode("view", "foreign/apps/calculator/view", brain, Scope, Ct));
    }

    [Fact]
    public async Task Desktop_Projection_Returns_Typed_Json_For_Signed_In_Owner()
    {
        var brain = new ScreenBrain();
        brain.RegisterDesktopShell(new FakeDesktopShell(new DesktopShellProjection(
            Windows:
            [
                new DesktopWindowProjection(
                    "w-1",
                    "calculator",
                    "Calculator",
                    "owner/apps/desktop/window/w-1",
                    [new DesktopTabProjection("tab-1", "calculator", "Calculator", true)],
                    "tab-1",
                    IsPinned: true,
                    IsFavourite: false),
            ],
            FocusedViewId: "tab-1",
            Pins: ["calculator"],
            Tiles: [new AppTile("calculator", "Calculator", "calculator", Category: "Tools", Dock: null, Launch: AppLaunch.Windowed, Kind: "app")],
            OpenWidgets: ["launcher", "signal"],
            StylePreset: "default",
            BarLayout: "bottom")));

        var caller = IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId(Scope));
        var result = await AppUiEndpoints.ReadDesktopProjection(brain, Scope, Ct, caller);
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var json = JsonSerializer.Serialize(valueResult.Value, AppUiEndpoints.ViewJson);

        Assert.Contains("\"focusedViewId\":\"tab-1\"", json);
        Assert.Contains("\"openWidgets\":[\"launcher\",\"signal\"]", json);
        Assert.Contains("\"viewportUri\":\"owner/apps/desktop/window/w-1\"", json);
        Assert.Contains("\"isPinned\":true", json);
    }

    [Fact]
    public async Task Desktop_Projection_Rejects_Anonymous_And_Foreign_Brain_Callers()
    {
        var brain = new ScreenBrain();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AppUiEndpoints.ReadDesktopProjection(brain, Scope, Ct, caller: null));

        var foreignCaller = IntoChat.OS.Tests.TestEdge.Authority.Person("foreign", "foreign", new BrainId("foreign"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AppUiEndpoints.ReadDesktopProjection(brain, Scope, Ct, foreignCaller));
    }

    [Fact]
    public async Task CompiledScreen_WithUnhostedRequiredSlot_ReturnsUnavailableWithoutActivatingOrFallingBackToUnitReader()
    {
        var brain = new TrackingBrain(new WorkspaceViewRef("v1", "owner/apps/customer-researcher", "main", "default"));
        var hostedNeurons = new StubHostedNeuronTypes(
            classes: new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                ["app.customer-researcher"] = typeof(IntoChat.OS.Apps.CustomerResearcher.CustomerResearcherApp),
            },
            hostedTypes: ["app.customer-researcher", "web.search"]);

        var viewportResult = await AppUiEndpoints.ReadNode(
            "viewport",
            "owner/apps/customer-researcher",
            brain,
            Scope,
            Ct,
            hostedNeurons: hostedNeurons);
        var viewportValue = Assert.IsAssignableFrom<IValueHttpResult>(viewportResult);
        var viewportJson = JsonSerializer.Serialize(viewportValue.Value, JsonSerializerOptions.Web);
        Assert.Contains("\"root\":null", viewportJson);
        Assert.Equal(0, brain.ScreenActivations);
        Assert.Equal(0, brain.UnitReaderCalls);

        var viewResult = await AppUiEndpoints.ReadNode(
            "view",
            "owner/apps/customer-researcher/view",
            brain,
            Scope,
            Ct,
            hostedNeurons: hostedNeurons);
        var viewValue = Assert.IsAssignableFrom<IValueHttpResult>(viewResult);
        Assert.Null(viewValue.Value);
        Assert.Equal(0, brain.ScreenActivations);
        Assert.Equal(0, brain.UnitReaderCalls);
    }

    private sealed class StubHostedNeuronTypes(
        IReadOnlyDictionary<string, Type> classes,
        IEnumerable<string> hostedTypes) : DigitalBrain.Components.IHostedNeuronTypes
    {
        private readonly HashSet<string> _hosted = new(hostedTypes, StringComparer.Ordinal);
        public bool IsHosted(string neuronType) => _hosted.Contains(neuronType);
        public bool TryGetNeuronClass(string neuronType, out Type? neuronClass) => classes.TryGetValue(neuronType, out neuronClass);
    }

    private sealed class TrackingBrain(WorkspaceViewRef target) : INeuronProxies
    {
        public int ScreenActivations { get; private set; }
        public int UnitReaderCalls { get; private set; }

        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IViewport))
            {
                return (T)(object)new TargetViewport(target);
            }
            if (typeof(T) == typeof(IScreen))
            {
                ScreenActivations++;
                throw new InvalidOperationException($"Unexpected IScreen activation for {id}");
            }
            if (typeof(T) == typeof(IViewReader))
            {
                UnitReaderCalls++;
                throw new InvalidOperationException($"Unexpected IViewReader fallback for {id}");
            }
            throw new InvalidOperationException($"Unexpected proxy {typeof(T).Name} for {id}");
        }
    }

    private sealed class TargetViewport(WorkspaceViewRef target) : IViewport
    {
        public Task<ViewportState> Read() => Task.FromResult(new ViewportState(target.App, 1L, target));
        public Task Set(WorkspaceViewRef? target, long expectedRevision) => Task.CompletedTask;
    }
}

