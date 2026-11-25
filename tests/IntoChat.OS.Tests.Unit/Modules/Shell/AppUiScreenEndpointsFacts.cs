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

    private static IDisposable AsPerson(string principal = "alice")
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person(principal, principal, new BrainId(Scope)));

    private static PublishedView CreateTestView(string alias, long generation, string key, string emitSignal)
    {
        var view = new View(
            "TestApp",
            new Column([
                new Button(key, "ClickMe", new Command(".", emitSignal, null)),
                new Conversation("btn-alien-thread", [], new ComposerNode("btn-alien", new Emit("app.alien", "test.alien", null)))
            ]));
        var json = JsonSerializer.SerializeToElement(view, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));
        return new PublishedView(alias, generation, json);
    }

    private sealed class FakeScreen : IScreen
    {
        private readonly PublishedView _published;
        public List<(string Alias, long Generation, string Key, string? Value, string? By)> Activations { get; } = [];

        public FakeScreen(PublishedView published)
        {
            _published = published;
        }

        public Task<PublishedView?> Open() => Read();

        public Task Act(ScreenAction action) => Activate(action.Alias ?? _published.Alias, action.Generation, action.NodeKey, action.FieldValue);

        public Task<PublishedView?> Read(string? alias = null)
            => Task.FromResult<PublishedView?>(_published);

        public Task Activate(string alias, long generation, string nodeKey, string? fieldValue = null, string? by = null)
        {
            if (generation != _published.Generation)
            {
                throw new ActivationRefusedException(ActivationRefusedException.Stale);
            }
            if (nodeKey == "btn-alien")
            {
                throw new ActivationRefusedException(ActivationRefusedException.ForeignOwner);
            }
            Activations.Add((alias, generation, nodeKey, fieldValue, by));
            return Task.CompletedTask;
        }

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
            throw new InvalidOperationException($"No {typeof(T).Name} in this mock brain.");
        }
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

    private sealed class EmptyHostedTypes : IHostedAppTypes
    {
        public bool IsHostLoaded(string appKey) => true;
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
    public async Task Mismatched_View_Generation_Throws_StaleActionException()
    {
        var brain = new ScreenBrain();
        var published = CreateTestView("app.calculator", 2L, "btn-0", "calc.press");
        var fakeScreen = new FakeScreen(published);
        brain.RegisterScreen("app.calculator", fakeScreen);

        // Client saw generation 1, but screen is at generation 2
        var uiEvent = new AppUiEndpoints.UiEvent("view", "owner/apps/calculator/view", Generation: 1L, Key: "btn-0");

        using (AsPerson("alice"))
        {
            var ex = await Assert.ThrowsAsync<StaleActionException>(() =>
                AppUiEndpoints.Deliver(uiEvent, brain, Scope, new EmptyHostedTypes(), Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
            Assert.Contains("This action is no longer available", ex.Message);
        }
    }

    [Fact]
    public async Task Button_Emitting_To_Foreign_Owner_Throws_UnauthorizedAccessException()
    {
        var brain = new ScreenBrain();
        var published = CreateTestView("app.calculator", 1L, "btn-alien", "test.alien");
        var fakeScreen = new FakeScreen(published);
        brain.RegisterScreen("app.calculator", fakeScreen);

        var uiEvent = new AppUiEndpoints.UiEvent("view", "owner/apps/calculator/view", Generation: 1L, Key: "btn-alien");

        using (AsPerson("alice"))
        {
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.Deliver(uiEvent, brain, Scope, new EmptyHostedTypes(), Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
            Assert.Contains("cannot emit to another owner", ex.Message);
        }
    }

    [Fact]
    public async Task Node_Activation_By_Unsigned_Caller_Throws_UnauthorizedAccessException()
    {
        var brain = new ScreenBrain();
        var published = CreateTestView("app.calculator", 1L, "btn-0", "calc.press");
        var fakeScreen = new FakeScreen(published);
        brain.RegisterScreen("app.calculator", fakeScreen);

        // No AsPerson() call — unsigned caller
        var uiEvent = new AppUiEndpoints.UiEvent("view", "owner/apps/calculator/view", Generation: 1L, Key: "btn-0");

        // REQUIREMENT: unsigned callers MUST be rejected with UnauthorizedAccessException
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AppUiEndpoints.Deliver(uiEvent, brain, Scope, new EmptyHostedTypes(), Ct, IntoChat.OS.Tests.TestEdge.Authority.Current));
    }

    [Fact]
    public async Task Node_Activation_By_Signed_Caller_Succeeds_And_Records_PersonId()
    {
        var brain = new ScreenBrain();
        var published = CreateTestView("app.calculator", 1L, "btn-0", "calc.press");
        var fakeScreen = new FakeScreen(published);
        brain.RegisterScreen("app.calculator", fakeScreen);

        var uiEvent = new AppUiEndpoints.UiEvent("view", "owner/apps/calculator/view", Generation: 1L, Key: "btn-0");

        using (AsPerson("alice"))
        {
            var result = await AppUiEndpoints.Deliver(uiEvent, brain, Scope, new EmptyHostedTypes(), Ct, IntoChat.OS.Tests.TestEdge.Authority.Current);
            Assert.NotNull(result);
        }

        var activation = Assert.Single(fakeScreen.Activations);
        Assert.Equal("app.calculator", activation.Alias);
        Assert.Equal(1L, activation.Generation);
        Assert.Equal("btn-0", activation.Key);
        Assert.Equal("alice", activation.By);
    }
}
