using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Tests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Catalog.Tests;

public sealed class AppsEndpointsRequestForwardingFacts
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("narrow", "true")]
    [InlineData("open", "photocraft")]
    [InlineData("focus", "w-1")]
    public void BuildScreenRequestArgsDoesNotMapRetiredDesktopScalarCases(string operation, string scalar)
    {
        var args = AppsEndpoints.BuildScreenRequestArgs("desktop", operation, scalar);
        Assert.NotNull(args);

        var root = args!.Value;
        Assert.Equal(scalar, root.GetProperty("input").GetString());
        Assert.False(root.TryGetProperty("narrow", out _));
        Assert.False(root.TryGetProperty("app", out _));
        Assert.False(root.TryGetProperty("windowId", out _));
    }

    [Fact]
    public void BuildScreenRequestArgsMapsSettingsNavigateToSectionField()
    {
        var args = AppsEndpoints.BuildScreenRequestArgs("settings", "navigate", "models");
        Assert.NotNull(args);

        var signal = args!.Value.Deserialize<SettingsNavigate>(JsonOptions);
        Assert.NotNull(signal);
        Assert.Equal("models", signal!.Section);
    }

    [Fact]
    public void BuildScreenRequestArgsPreservesJsonObjectPayloads()
    {
        var args = AppsEndpoints.BuildScreenRequestArgs("desktop", "open", """{"app":"ino","view":"chat"}""");
        Assert.NotNull(args);

        var signal = args!.Value.Deserialize<DesktopOpen>(JsonOptions);
        Assert.NotNull(signal);
        Assert.Equal("ino", signal!.App);
        Assert.Equal("chat", signal.View);
    }

    [Fact]
    public void WorkspaceAppsJsonContextPreservesWireShapes()
    {
        var catalogJson = JsonSerializer.Serialize(
            new List<AppCatalogItemResponse>
            {
                new(
                    Name: "notes",
                    Title: "Notes",
                    Description: "Simple notes",
                    Status: "Live",
                    Version: "1.0.0",
                    Operations: ["open"],
                    Icon: "icon.svg",
                    PublishedVersion: "1.0.0",
                    Origin: new AppCatalogOriginResponse("intochat", "notes", "1.0.0"),
                    Edited: false,
                    UpdateAvailable: true,
                    ShippedUpdate: false),
            },
            WorkspaceAppsJsonContext.Default.ListAppCatalogItemResponse);
        using (var doc = JsonDocument.Parse(catalogJson))
        {
            var item = doc.RootElement[0];
            Assert.Equal("notes", item.GetProperty("name").GetString());
            Assert.Equal("Notes", item.GetProperty("title").GetString());
            Assert.Equal("Live", item.GetProperty("status").GetString());
            Assert.Equal("intochat", item.GetProperty("origin").GetProperty("publisher").GetString());
            Assert.True(item.GetProperty("updateAvailable").GetBoolean());
            Assert.False(item.GetProperty("shippedUpdate").GetBoolean());
        }

        var installOkJson = JsonSerializer.Serialize(
            new AppInstallOkResponse("ok", "notes"),
            WorkspaceAppsJsonContext.Default.AppInstallOkResponse);
        Assert.Equal("""{"status":"ok","installedName":"notes"}""", installOkJson);

        var deletedJson = JsonSerializer.Serialize(
            new AppDeletedResponse("notes"),
            WorkspaceAppsJsonContext.Default.AppDeletedResponse);
        Assert.Equal("""{"deleted":"notes"}""", deletedJson);

        var acceptedJson = JsonSerializer.Serialize(
            new AppRequestAcceptedResponse("req-1"),
            WorkspaceAppsJsonContext.Default.AppRequestAcceptedResponse);
        Assert.Equal("""{"requestId":"req-1"}""", acceptedJson);

        var retiredJson = JsonSerializer.Serialize(
            new PolicyRetiredResponse("pol-1"),
            WorkspaceAppsJsonContext.Default.PolicyRetiredResponse);
        Assert.Equal("""{"retired":"pol-1"}""", retiredJson);

        var compileFailJson = JsonSerializer.Serialize(
            new PackageCompileResponse(false, ["missing slot"], null, null),
            WorkspaceAppsJsonContext.Default.PackageCompileResponse);
        using (var doc = JsonDocument.Parse(compileFailJson))
        {
            Assert.False(doc.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal("missing slot", doc.RootElement.GetProperty("errors")[0].GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("package").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("slots").ValueKind);
        }

        var ruleJson = JsonSerializer.Serialize(
            new RuleViolationResponse("open", ["WindowLimit"], ["Too many windows."]),
            WorkspaceAppsJsonContext.Default.RuleViolationResponse);
        Assert.Equal("""{"operation":"open","rules":["WindowLimit"],"messages":["Too many windows."]}""", ruleJson);
    }

    [Fact]
    public async Task ReadViewAsyncUsesScreenViewResolverAndAppliesChildKeysScreen()
    {
        var brainId = new BrainId("owner");
        var resolvedId = new NeuronId(brainId, "agentapp", "custom");
        var viewJson = JsonDocument.Parse(
            """{"title":"Custom","root":{"kind":"column","children":[{"key":"c1","kind":"neuronRef","refKind":"child","neuronUri":"owner/agentapp/custom/panel"}]}}""")
            .RootElement.Clone();
        var published = new PublishedView("agentapp", 3L, viewJson);

        var brain = new StubBrain();
        brain.RegisterScreen(resolvedId, new StubScreen(published));
        var resolver = new StubTargetResolver(new ScreenTarget(resolvedId, IScreenTargetResolver.AgentAppKind));

        var result = await AppsEndpoints.ReadViewAsync("custom", brain, "owner", Ct, resolver: resolver);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync(Ct);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("agentapp", doc.RootElement.GetProperty("alias").GetString());
        Assert.Equal(3L, doc.RootElement.GetProperty("generation").GetInt64());
        var view = doc.RootElement.GetProperty("view");
        Assert.Equal(View.ActRoute, view.GetProperty("acts").GetString());
        Assert.Equal("panel", view.GetProperty("root").GetProperty("children")[0].GetProperty("neuronUri").GetString());
    }

    [Fact]
    public async Task ReadViewAsyncSkipsActivationWhenHostedAppTypesReportsNotLoaded()
    {
        var brain = new StubBrain();
        var hosted = new StubHostedAppTypes(loaded: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            AppsEndpoints.ReadViewAsync("calculator", brain, "owner", Ct, hosted: hosted));
        Assert.Equal(0, brain.ScreenLookups);
    }

    [Fact]
    public async Task ForwardScreenRequestDelegatesTargetResolutionToScreenViewResolver()
    {
        var brainId = new BrainId("owner");
        var resolvedId = new NeuronId(brainId, "agentapp", "custom");
        var screen = new StubScreen(new PublishedView("agentapp", 1L, JsonDocument.Parse("{}").RootElement.Clone()));
        var brain = new StubBrain();
        brain.RegisterScreen(resolvedId, screen);
        var resolver = new StubTargetResolver(new ScreenTarget(resolvedId, IScreenTargetResolver.AgentAppKind));

        var forwarded = await AppsEndpoints.ForwardScreenRequest(
            brain,
            "owner",
            "custom",
            "open",
            "inbox",
            hosted: null,
            resolver: resolver,
            hostedNeurons: null,
            Ct);

        Assert.True(forwarded);
        Assert.Equal("custom.open", screen.LastSignal);
        Assert.Equal("inbox", screen.LastArgs!.Value.GetProperty("input").GetString());
    }

    [Fact]
    public async Task ForwardScreenRequestReturnsFalseWhenNotLoaded()
    {
        var brain = new StubBrain();
        var hosted = new StubHostedAppTypes(loaded: false);

        var forwarded = await AppsEndpoints.ForwardScreenRequest(
            brain,
            "owner",
            "calculator",
            "open",
            "1",
            hosted: hosted,
            resolver: null,
            hostedNeurons: null,
            Ct);

        Assert.False(forwarded);
        Assert.Equal(0, brain.ScreenLookups);
    }

    private sealed class StubTargetResolver(ScreenTarget? target) : IScreenTargetResolver
    {
        public Task<ScreenTarget?> ResolveAsync(BrainId brainId, string address, CancellationToken ct = default)
            => Task.FromResult(target);
    }

    private sealed class StubHostedAppTypes(bool loaded) : IHostedAppTypes
    {
        public bool IsHostLoaded(string appKey) => loaded;
    }

    private sealed class StubScreen(PublishedView published) : IScreen
    {
        public string? LastSignal { get; private set; }
        public JsonElement? LastArgs { get; private set; }

        public Task<PublishedView?> Open() => Read();
        public Task<PublishedView?> Read(string? alias = null) => Task.FromResult<PublishedView?>(published);
        public Task Request(string signal, JsonElement? args = null)
        {
            LastSignal = signal;
            LastArgs = args;
            return Task.CompletedTask;
        }
        public Task<PublishedView?> ReadChild(string path) => Task.FromResult<PublishedView?>(null);
        public Task Dispatch(Command command) => Task.CompletedTask;
    }

    private sealed class StubBrain : INeuronProxies
    {
        private readonly Dictionary<NeuronId, IScreen> _screens = new();
        public int ScreenLookups { get; private set; }

        public void RegisterScreen(NeuronId id, IScreen screen) => _screens[id] = screen;

        public T Get<T>(NeuronId id) where T : class
        {
            if (typeof(T) == typeof(IScreen))
            {
                ScreenLookups++;
                if (_screens.TryGetValue(id, out var screen))
                {
                    return (T)screen;
                }
                throw new InvalidOperationException($"No screen for {id}");
            }
            if (typeof(T) == typeof(IView))
            {
                throw new InvalidOperationException($"No view for {id}");
            }
            throw new InvalidOperationException($"Unexpected {typeof(T).Name} for {id}");
        }
    }

    [Fact]
    public void EnterKernelSetsKernelActorAndDisposeRestoresOuterCaller()
    {
        var edge = TestEdge.Authority;
        using var outer = edge.Enter(edge.Person("ada", "ada", new BrainId("owner")));
        Assert.False(edge.IsKernelActive);

        using (edge.EnterKernel(new BrainId("owner")))
        {
            Assert.True(edge.IsKernelActive);
            Assert.IsType<Actor.Kernel>(edge.Current?.Actor);
            Assert.Equal("owner", edge.Current!.Brain.Value);
        }

        Assert.False(edge.IsKernelActive);
        Assert.IsType<Actor.Person>(edge.Current?.Actor);
    }

    [Fact]
    public void EnterAppSetsAppActorAndDisposeRestoresEmptyCaller()
    {
        var edge = TestEdge.Authority;
        using var cleared = edge.ClearCaller();
        Assert.Null(edge.Current);

        var caller = edge.App("owner/apps/files", new Actor.Person("owner", "owner"), new BrainId("owner"));
        using (edge.Enter(caller))
        {
            Assert.False(edge.IsKernelActive);
            var app = Assert.IsType<Actor.App>(edge.Current?.Actor);
            Assert.Equal("owner/apps/files", app.Key);
            Assert.Equal("owner", app.For.Principal);
            Assert.Equal("owner", edge.Current!.Brain.Value);
        }

        Assert.Null(edge.Current);
    }
}
