using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.Aspire;
using IntoChat.Aspire.Screens;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

// The Aspire app (app.aspire), a screen of the Aspire component since it left the shared IntoChat.OS.Apps assembly.
public sealed class AspireAppFacts
{
    // typeof(AspireApp).AssemblyQualifiedName before the move, as every brain created before it stores in __CompiledType.
    private const string PreMoveCompiledType = "IntoChat.OS.Apps.Aspire.AspireApp, IntoChat.OS.Apps, Version=0.23.3.0, Culture=neutral, PublicKeyToken=null";

    // The shipped app names a brain got in the default composition before the move (Catalog, the shell apps and, in it then, aspire).
    private static readonly string[] PreMoveShippedApps =
    [
        "artcraft", "aspire", "cadcraft", "calculator", "creator", "customer-researcher", "data", "deckcraft", "designcraft", "desktop", "devui",
        "effectcraft", "engineer", "figcraft", "files", "filmcraft", "friends", "gallery", "gridcraft", "ino", "lightcraft", "markdraw",
        "materialcraft", "orleans", "pdfcraft", "photocraft", "settings", "solidcraft", "soundcraft", "tasks", "texturecraft", "usage",
        "vectorcraft", "widgetbook", "wordcraft",
    ];

    private static readonly BrainId TestBrain = new("infra-test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IDisposable EnterPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", TestBrain));

    private static IEnumerable<Assembly> RegisteredBy(params Action<IServiceCollection, IConfiguration>[] modules)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        foreach (var configure in modules) { configure(services, configuration); }
        return services.BuildServiceProvider().GetServices<CompiledAppsRegistration>().Select(registration => registration.Assembly).ToArray();
    }

    private static FeatureCatalog DefaultCatalog()
        => new(RegisteredBy(AppsModule.ConfigureServices, ShippedShellAppsModule.ConfigureServices, AspireModule.ConfigureServices));

    [Fact]
    public async Task TheAspireAppIsShippedByTheAspireComponent()
    {
        var byComponent = await new FeatureCatalog(RegisteredBy(AspireModule.ConfigureServices)).Bundles();
        var byShellApps = await new FeatureCatalog(RegisteredBy(ShippedShellAppsModule.ConfigureServices)).Bundles();

        Assert.Contains(("os", "aspire"), byComponent.Keys);
        Assert.DoesNotContain(("os", "aspire"), byShellApps.Keys);
        Assert.Equal(typeof(AspireApp).AssemblyQualifiedName, byComponent[("os", "aspire")].CompiledType);
    }

    [Fact]
    public async Task TheDefaultCompositionShipsTheSameAppsAsBeforeTheMoveWithAspireOnce()
    {
        var catalog = DefaultCatalog();

        var names = (await catalog.Bundles()).Keys.Where(key => key.Owner == "os").Select(key => key.Name).ToArray();

        Assert.Empty(await catalog.Failures());
        Assert.Single(names, name => name == "aspire");
        Assert.Equal(PreMoveShippedApps, names);
        Assert.Contains(("os", "aspire"), await catalog.BootSet());
        var origin = Assert.IsType<FeatureOrigin.ModuleSource>(await catalog.Origin("os", "aspire"));
        Assert.Same(typeof(AspireModule).Assembly, origin.Assembly);
    }

    private static TestOS RecordOs()
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHostedAppTypes>(new CatalogHostedAppTypes(DefaultCatalog()));
        return TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();
    }

    private static NeuronId AppRecord(BrainId brain)
        => new(brain, IntoChat.OS.Host.Primitives.AppGrainType.Name, IntoChat.OS.Host.Primitives.AppRef.Of(brain, "aspire").Key);

    private static AppOrigin ShippedOrigin(string version) => new(ShippedApps.Publisher, new AppSource.Shipped(), "aspire", version);

    private static async Task<AppSnapshot> Restored(TestOS os, NeuronId id)
    {
        await os.Deactivate(id);
        var app = os.Get<IApp>(id);
        _ = await app.Read();
        await os.Idle();
        await Task.Delay(100, Ct);
        await os.Idle();
        return await app.Read();
    }

    // An existing brain's shipped, unedited record keeps the pre-move type name (nothing rewrites it) and still restores and deploys.
    [Fact]
    public async Task AnExistingShippedRecordWithThePreMoveTypeNameStaysLive()
    {
        using var person = EnterPerson();
        await using var os = RecordOs();
        var id = AppRecord(TestBrain);
        var app = os.Get<IApp>(id);
        var preMove = AspireApp.Bundle with { CompiledType = PreMoveCompiledType };

        var imported = await app.Import(preMove, ShippedOrigin(preMove.Version));
        Assert.Equal(AppStatus.Live, imported.Status);

        // What the host does at every boot: load the shipped bundle again (same version, spec and files).
        var reloaded = await app.Replace(AspireApp.Bundle, ShippedOrigin(AspireApp.Bundle.Version));
        var restored = await Restored(os, id);

        Assert.Equal(AppStatus.Live, reloaded.Status);
        Assert.Equal(AppStatus.Live, restored.Status);
        Assert.Equal(PreMoveCompiledType, restored.Settings[AppTrust.CompiledTypeSetting]);
        Assert.DoesNotContain(restored.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
    }

    // An edited copy is the person's own program: its stored type name must resolve, so a pre-move name fails as it always would have.
    [Fact]
    public async Task AnEditedCopyWithThePreMoveTypeNameFailsAsBefore()
    {
        using var person = EnterPerson();
        await using var os = RecordOs();
        var id = AppRecord(TestBrain);
        var app = os.Get<IApp>(id);
        await app.Import(AspireApp.Bundle, ShippedOrigin(AspireApp.Bundle.Version));
        await app.SaveSpec("my own aspire", Guid.NewGuid());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.Replace(AspireApp.Bundle with { CompiledType = PreMoveCompiledType, Version = "1.2.0", Spec = "my own aspire" }));

        Assert.Equal($"Compiled app type '{PreMoveCompiledType}' could not be resolved.", error.Message);
        var seen = await app.Read();
        Assert.Equal(AppStatus.Failed, seen.Status);
        Assert.Contains(seen.Activity, line => line.Contains("could not be resolved", StringComparison.Ordinal));
    }

    [Fact]
    public void AspireApp_HasZeroMutableInstanceFields_And_RegisteredInNativeCatalog()
    {
        var aspireFields = typeof(AspireApp)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static f => !f.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .ToArray();
        Assert.Empty(aspireFields);
        var manifest = DigitalBrain.Components.ComponentManifests.All.Single(m => m.Id.Value == "app.aspire");
        Assert.Equal("Developer", manifest.Face?.Category);
    }

    [Fact]
    public void AspireApp_ResolvesEndpointFromConfigurationKeysAndSemicolonLists_RefusingInvalidUrls()
    {
        var unconfigured = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var (defaultUrl, isDefaultConfigured) = AspireApp.ResolveEndpoint(unconfigured);
        Assert.Equal(AspireAppState.DefaultBaseUrl, defaultUrl);
        Assert.False(isDefaultConfigured);

        var explicitConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AspireAppState.ConfiguredUrlKey] = "https://aspire.dev.internal:18888/",
            })
            .Build();
        var (fromPrimary, primaryConfigured) = AspireApp.ResolveEndpoint(explicitConfig);
        Assert.Equal("https://aspire.dev.internal:18888", fromPrimary);
        Assert.True(primaryConfigured);

        var envStyleConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AspireAppState.DashboardUrlEnvKey] = "http://localhost:15100/",
            })
            .Build();
        var (fromEnvKey, envConfigured) = AspireApp.ResolveEndpoint(envStyleConfig);
        Assert.Equal("http://localhost:15100", fromEnvKey);
        Assert.True(envConfigured);

        var multiUrlConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AspireAppState.DotnetDashboardUrlsKey] = "ftp://ignored.invalid;https://localhost:17197;http://localhost:15100",
            })
            .Build();
        var (fromList, listConfigured) = AspireApp.ResolveEndpoint(multiUrlConfig);
        Assert.Equal("https://localhost:17197", fromList);
        Assert.True(listConfigured);

        var badConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AspireAppState.ConfiguredUrlKey] = "javascript:alert(1)",
            })
            .Build();
        var (fromBad, badConfigured) = AspireApp.ResolveEndpoint(badConfig);
        Assert.Equal(AspireAppState.DefaultBaseUrl, fromBad);
        Assert.False(badConfigured);
    }

    [Fact]
    public void AspireAppState_AggregateExplorer_UnfoldsZeroViolations_FullBleedWebBrowser_AndRuleGuards()
    {
        var graph = AggregateExplorer<AspireAppState>.ExploreScreen<AspireApp>(maxStates: 64).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.Empty(graph.Violations);

        foreach (var section in AspireAppState.SupportedSections)
        {
            Assert.Contains(graph.States, s => s.State.Section == section);
        }

        Assert.Contains(graph.States, s => s.State.Section == "traces" && s.State.TraceId == "4bf92f3577b34da6a3ce929d0e0e4736");
        Assert.Contains(graph.States, s => s.State.ReloadRevision == 1);
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_section", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_trace_id", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_dashboard_url", StringComparer.Ordinal));

        var preview = AggregateExplorer<AspireAppState>.PreviewNode<AspireApp>(new AspireAppState());
        var shell = Assert.IsType<AppShellNode>(preview);
        Assert.Equal("aspire-shell", shell.Key);
        Assert.Null(shell.Sidebar);
        Assert.Null(shell.Inspector);

        var browser = Assert.IsType<WebBrowser>(shell.Body);
        Assert.Equal("aspire-browser", browser.Key);
        Assert.Equal(920, browser.Height);
        Assert.Equal("Aspire Dashboard", browser.AutomationName);
        Assert.Equal("http://localhost:18888/resources", browser.Url);

        var wire = JsonSerializer.Serialize(preview, RuntimeProtocol.OptionsFor(typeof(Node)));
        Assert.DoesNotContain("\"mono\":true", wire, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AspireApp_SupportsINavigatedToTraceDeepLinks_DIHydration_AndINavigatedFromGuards()
    {
        using var person = EnterPerson();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AspireAppState.ConfiguredUrlKey] = "http://localhost:19888",
            })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        os.RegisterCatalog();
        os.Register<AspireApp>();

        var screen = os.Get<IScreen>(new NeuronId(TestBrain, "app.aspire", "default"));
        var opened = await screen.Open();
        Assert.NotNull(opened);
        Assert.Contains("http://localhost:19888/resources", opened!.View.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Live \\u00B7 localhost:19888", opened.View.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var traceArgsJson = JsonSerializer.Serialize(
            new BrowserNavArgs(TraceId: "4bf92f3577b34da6a3ce929d0e0e4736"),
            RuntimeProtocol.OptionsFor(typeof(BrowserNavArgs)));
        await screen.Request(new UiNavigateTo(
            ArgsJson: traceArgsJson,
            FromNeuron: "app.engineer",
            Transition: ViewTransition.SharedAxisZ));
        await os.Idle();

        var traceWire = (await screen.Open())!.View.GetRawText();
        Assert.Contains("http://localhost:19888/traces/detail/4bf92f3577b34da6a3ce929d0e0e4736", traceWire, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"shared-axis-z\"", traceWire, StringComparison.Ordinal);

        var badUrlJson = JsonSerializer.Serialize(
            new BrowserNavArgs(BaseUrl: "javascript:alert(1)"),
            RuntimeProtocol.OptionsFor(typeof(BrowserNavArgs)));
        await screen.Request(new UiNavigateTo(ArgsJson: badUrlJson, FromNeuron: "app.engineer"));
        await os.Idle();

        var badUrlWire = (await screen.Open())!.View.GetRawText();
        Assert.Contains("http://localhost:19888/traces/detail/4bf92f3577b34da6a3ce929d0e0e4736", badUrlWire, StringComparison.Ordinal);
        Assert.Contains("invalid_dashboard_url", badUrlWire, StringComparison.Ordinal);

        var installed = await os.Install<AspireApp>();
        Assert.True(await installed.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer"), Ct));
        await installed.App.NavigateToAsync(new BrowserNavArgs(Route: "/traces/detail/00f067aa0ba902b7"), "app.engineer", ViewTransition.CameraFlyTo, cancellationToken: Ct);
        Assert.Equal("traces", installed.App.State.Section);
        Assert.Equal("00f067aa0ba902b7", installed.App.State.TraceId);

        var lockCmd = RuntimeProtocol.SerializeToElement(
            new UiCommand("state", nameof(AspireAppState.SetNavigationLock), [JsonSerializer.SerializeToElement(true)]),
            typeof(UiCommand));
        await screen.Request(UiCommand.SignalAlias, lockCmd);
        await os.Idle();

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() =>
            screen.Dispatch(new Command(
                ".",
                UiNavigateFrom.SignalAlias,
                RuntimeProtocol.SerializeToElement(
                    new UiNavigateFrom(ToNeuron: "app.engineer", Force: false),
                    typeof(UiNavigateFrom)))));
        Assert.Contains("Rule:navigation_locked:", refused.Reason, StringComparison.Ordinal);
        await os.Idle();
        Assert.Contains("navigation_locked", (await screen.Open())!.View.GetRawText(), StringComparison.Ordinal);

        await screen.Dispatch(new Command(
            ".",
            UiNavigateFrom.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "app.engineer", Force: true),
                typeof(UiNavigateFrom))));
        await os.Idle();
        Assert.DoesNotContain("navigation_locked", (await screen.Open())!.View.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UiIntrospector_ExploresAspireStateGraph_WithValidGraphvizDot()
    {
        using var person = EnterPerson();
        await using var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));

        var aspireGraph = await introspector.ExploreStateGraph(typeof(AspireAppState).FullName!, maxStates: 64);
        Assert.True(aspireGraph.IsHealthy);
        Assert.True(aspireGraph.States.Length >= 5);
        Assert.Contains(aspireGraph.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_section"));
        Assert.Contains(aspireGraph.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_dashboard_url"));
        Assert.Contains("digraph AspireAppState", aspireGraph.Dot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossWindowOpenAppIntents_PlainInputs_DeepLinkAspire()
    {
        using var person = EnterPerson();
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register<AspireApp>();

        var aspire = os.Get<IScreen>(new NeuronId(TestBrain, "app.aspire", "default"));
        _ = await aspire.Open();

        // 1. Plain hex trace input via os://window/open?app=aspire&input=<hexTraceId> -> traces/detail/{traceId}
        var aspireHexQuery = ParseQuery(new Uri(AppSignals.OpenApp("aspire", "4bf92f3577b34da6a3ce929d0e0e4736").Arg!).Query);
        await aspire.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(FromNeuron: "infra-test/apps/engineer", Transition: ViewTransition.FadeThrough, Input: aspireHexQuery["input"]),
                typeof(UiNavigateTo))));
        await os.Idle();
        var aspireHexWire = (await aspire.Open())!.View.GetRawText();
        Assert.Contains("http://localhost:18888/traces/detail/4bf92f3577b34da6a3ce929d0e0e4736", aspireHexWire, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"fade-through\"", aspireHexWire, StringComparison.Ordinal);

        // 2. General flow id via os://window/open?app=aspire&input=flow-mission-7 -> traces section overview
        var aspireFlowQuery = ParseQuery(new Uri(AppSignals.OpenApp("aspire", "flow-mission-7").Arg!).Query);
        await aspire.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(FromNeuron: "infra-test/apps/engineer", Transition: ViewTransition.SharedAxisX, Input: aspireFlowQuery["input"]),
                typeof(UiNavigateTo))));
        await os.Idle();
        var aspireFlowWire = (await aspire.Open())!.View.GetRawText();
        Assert.Contains("\"url\":\"http://localhost:18888/traces\"", aspireFlowWire, StringComparison.Ordinal);
        Assert.DoesNotContain("traces/detail/", aspireFlowWire, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"shared-axis-x\"", aspireFlowWire, StringComparison.Ordinal);

        // AspireOpen(Input: ...) from /apps/aspire/request forwarding
        await aspire.Request(new AspireOpen(Input: "00f067aa0ba902b7"));
        await os.Idle();
        Assert.Contains("http://localhost:18888/traces/detail/00f067aa0ba902b7", (await aspire.Open())!.View.GetRawText(), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQuery(string rawQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return result;
    }
}
