using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Aspire;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Orleans;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Apps.Tests.Unit.Infra;

public sealed class AspireAndOrleansAppsFacts
{
    private static readonly BrainId TestBrain = new("infra-test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IDisposable EnterPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", TestBrain));

    [Fact]
    public void AspireApp_And_OrleansApp_HaveZeroMutableInstanceFields_And_RegisteredInNativeCatalog()
    {
        var aspireFields = typeof(AspireApp)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static f => !f.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .ToArray();
        Assert.Empty(aspireFields);

        var orleansFields = typeof(OrleansApp)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static f => !f.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .ToArray();
        Assert.Empty(orleansFields);

        Assert.Contains(DesktopApp.NativeCatalog, c => c.Id == "aspire" && c.Category == "Developer" && c.RuntimeMode == "orleans-grain");
        Assert.Contains(DesktopApp.NativeCatalog, c => c.Id == "orleans" && c.Category == "Developer" && c.RuntimeMode == "orleans-grain");
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
    public void OrleansAppState_AggregateExplorer_UnfoldsZeroViolations_FullBleedWebBrowser_AndRuleGuards()
    {
        var graph = AggregateExplorer<OrleansAppState>.ExploreScreen<OrleansApp>(maxStates: 64).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.Empty(graph.Violations);

        foreach (var section in OrleansAppState.SupportedSections)
        {
            Assert.Contains(graph.States, s => s.State.Section == section);
        }

        Assert.Contains(graph.States, s => s.State.Section == "grains" && s.State.GrainFilter == "AgentNeuron");
        Assert.Contains(graph.States, s => s.State.ReloadRevision == 1);
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_section", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_grain_filter", StringComparer.Ordinal));

        var preview = AggregateExplorer<OrleansAppState>.PreviewNode<OrleansApp>(new OrleansAppState());
        var shell = Assert.IsType<AppShellNode>(preview);
        Assert.Equal("orleans-shell", shell.Key);
        Assert.Null(shell.Sidebar);
        Assert.Null(shell.Inspector);

        var browser = Assert.IsType<WebBrowser>(shell.Body);
        Assert.Equal("orleans-browser", browser.Key);
        Assert.Equal(920, browser.Height);
        Assert.Equal("Orleans Grain Dashboard", browser.AutomationName);
        Assert.Equal("/orleans#/overview", browser.Url);

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
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(AspireApp).Assembly);

        var screen = os.Get<IScreen>(new NeuronId(TestBrain, "app.aspire", "default"));
        var opened = await screen.Open();
        Assert.NotNull(opened);
        Assert.Contains("http://localhost:19888/resources", opened!.View.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("Live \\u00B7 localhost:19888", opened.View.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // Deep-link via UiNavigateTo with TraceId + SharedAxisZ transition
        var traceArgsJson = JsonSerializer.Serialize(
            new BrowserNavArgs(TraceId: "4bf92f3577b34da6a3ce929d0e0e4736"),
            RuntimeProtocol.OptionsFor(typeof(BrowserNavArgs)));
        await screen.Request(new UiNavigateTo(
            ArgsJson: traceArgsJson,
            FromNeuron: "app.engineer",
            Transition: ViewTransition.SharedAxisZ));
        await os.Idle();

        var traceView = await screen.Open();
        var traceWire = traceView!.View.GetRawText();
        Assert.Contains("http://localhost:19888/traces/detail/4bf92f3577b34da6a3ce929d0e0e4736", traceWire, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"shared-axis-z\"", traceWire, StringComparison.Ordinal);

        // Malformed URL via UiNavigateTo is refused by Rule without mutating the valid URL
        var badUrlJson = JsonSerializer.Serialize(
            new BrowserNavArgs(BaseUrl: "javascript:alert(1)"),
            RuntimeProtocol.OptionsFor(typeof(BrowserNavArgs)));
        await screen.Request(new UiNavigateTo(ArgsJson: badUrlJson, FromNeuron: "app.engineer"));
        await os.Idle();

        var afterBadUrl = await screen.Open();
        var badUrlWire = afterBadUrl!.View.GetRawText();
        Assert.Contains("http://localhost:19888/traces/detail/4bf92f3577b34da6a3ce929d0e0e4736", badUrlWire, StringComparison.Ordinal);
        Assert.Contains("invalid_dashboard_url", badUrlWire, StringComparison.Ordinal);

        // Verify typed NavigateToAsync & TryNavigateFromAsync on attached TestOS app instance
        var installed = await os.Install<AspireApp>();
        Assert.True(await installed.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer"), Ct));
        await installed.App.NavigateToAsync(new BrowserNavArgs(Route: "/traces/detail/00f067aa0ba902b7"), "app.engineer", ViewTransition.CameraFlyTo, cancellationToken: Ct);
        Assert.Equal("traces", installed.App.State.Section);
        Assert.Equal("00f067aa0ba902b7", installed.App.State.TraceId);

        // Lock navigation on live grain -> UiNavigateFrom refuses without Force, accepts with Force
        var lockCmd = RuntimeProtocol.SerializeToElement(
            new UiCommand("state", nameof(AspireAppState.SetNavigationLock), [JsonSerializer.SerializeToElement(true)]),
            typeof(UiCommand));
        await screen.Request(UiCommand.SignalAlias, lockCmd);
        await os.Idle();

        await screen.Request(new UiNavigateFrom(ToNeuron: "app.engineer", Force: false));
        await os.Idle();
        var lockedWire = (await screen.Open())!.View.GetRawText();
        Assert.Contains("navigation_locked", lockedWire, StringComparison.Ordinal);

        await screen.Request(new UiNavigateFrom(ToNeuron: "app.engineer", Force: true));
        await os.Idle();
        var forcedWire = (await screen.Open())!.View.GetRawText();
        Assert.DoesNotContain("navigation_locked", forcedWire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrleansApp_SupportsINavigatedToGrainDeepLinks_SectionSwitching_AndINavigatedFromGuards()
    {
        using var person = EnterPerson();
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(OrleansApp).Assembly);

        var screen = os.Get<IScreen>(new NeuronId(TestBrain, "app.orleans", "default"));
        var opened = await screen.Open();
        Assert.NotNull(opened);
        Assert.Contains("/orleans#/overview", opened!.View.GetRawText(), StringComparison.Ordinal);

        // Switch section via pill button activation ("orleans:section:silos")
        await screen.Activate("app.orleans", opened.Generation, "orleans:section:silos");
        await os.Idle();
        var silosView = await screen.Open();
        Assert.Contains("/orleans#/silos", silosView!.View.GetRawText(), StringComparison.Ordinal);

        // Deep-link via UiNavigateTo to a specific grain type
        var grainArgsJson = JsonSerializer.Serialize(
            new BrowserNavArgs(GrainFilter: "DigitalBrain.Neurons.AgentNeuron"),
            RuntimeProtocol.OptionsFor(typeof(BrowserNavArgs)));
        await screen.Request(new UiNavigateTo(
            ArgsJson: grainArgsJson,
            FromNeuron: "app.creator",
            Transition: ViewTransition.FadeThrough));
        await os.Idle();

        var grainView = await screen.Open();
        var grainWire = grainView!.View.GetRawText();
        Assert.Contains("/orleans#/grains/DigitalBrain.Neurons.AgentNeuron", grainWire, StringComparison.Ordinal);
        Assert.Contains("Grain: DigitalBrain.Neurons.AgentNeuron", grainWire, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"fade-through\"", grainWire, StringComparison.Ordinal);

        // Unsafe grain filter is refused by Rule
        var badGrainJson = JsonSerializer.Serialize(
            new BrowserNavArgs(GrainFilter: "../bad?inject=1"),
            RuntimeProtocol.OptionsFor(typeof(BrowserNavArgs)));
        await screen.Request(new UiNavigateTo(ArgsJson: badGrainJson, FromNeuron: "app.creator"));
        await os.Idle();
        var badGrainWire = (await screen.Open())!.View.GetRawText();
        Assert.Contains("invalid_grain_filter", badGrainWire, StringComparison.Ordinal);
        Assert.Contains("/orleans#/grains/DigitalBrain.Neurons.AgentNeuron", badGrainWire, StringComparison.Ordinal);

        // Clear grain filter via toolbar button
        var beforeClear = await screen.Open();
        await screen.Activate("app.orleans", beforeClear!.Generation, "orleans:clear-filter");
        await os.Idle();
        var clearedWire = (await screen.Open())!.View.GetRawText();
        Assert.Contains("/orleans#/grains", clearedWire, StringComparison.Ordinal);
        Assert.DoesNotContain("/orleans#/grains/DigitalBrain.Neurons.AgentNeuron", clearedWire, StringComparison.Ordinal);

        // Verify typed NavigateToAsync & TryNavigateFromAsync + lock guard on attached TestOS app instance
        var installed = await os.Install<OrleansApp>();
        Assert.True(await installed.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer"), Ct));
        await installed.App.NavigateToAsync(new BrowserNavArgs(Route: "/orleans#/reminders"), "app.engineer", ViewTransition.SharedAxisX, cancellationToken: Ct);
        Assert.Equal("reminders", installed.App.State.Section);
        Assert.Equal("/orleans#/reminders", installed.App.State.CurrentUrl);
    }

    [Fact]
    public async Task UiIntrospector_ExploresAspireAndOrleansStateGraphs_WithValidGraphvizDot()
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

        var orleansGraph = await introspector.ExploreStateGraph(typeof(OrleansAppState).FullName!, maxStates: 64);
        Assert.True(orleansGraph.IsHealthy);
        Assert.True(orleansGraph.States.Length >= 4);
        Assert.Contains(orleansGraph.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_section"));
        Assert.Contains(orleansGraph.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_grain_filter"));
        Assert.Contains("digraph OrleansAppState", orleansGraph.Dot, StringComparison.Ordinal);
    }
}
