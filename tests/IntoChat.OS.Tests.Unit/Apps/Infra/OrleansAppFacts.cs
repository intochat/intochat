using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps;
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

// The Orleans dashboard app. The Aspire app beside it moved to the Aspire component (Components/Aspire/AspireAppFacts).
public sealed class OrleansAppFacts
{
    private static readonly BrainId TestBrain = new("infra-test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IDisposable EnterPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", TestBrain));

    [Fact]
    public void OrleansApp_HasZeroMutableInstanceFields_And_RegisteredInNativeCatalog()
    {
        var orleansFields = typeof(OrleansApp)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static f => !f.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .ToArray();
        Assert.Empty(orleansFields);

        var manifest = DigitalBrain.Components.ComponentManifests.All.Single(m => m.Id.Value == "app.orleans");
        Assert.Equal("Developer", manifest.Face?.Category);
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
    public async Task OrleansApp_SupportsINavigatedToGrainDeepLinks_SectionSwitching_AndINavigatedFromGuards()
    {
        using var person = EnterPerson();
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(OrleansApp).Assembly);

        var screen = os.Get<IScreen>(new NeuronId(TestBrain, "app.orleans", "default"));
        var opened = await screen.Open();
        Assert.NotNull(opened);
        Assert.Contains("/orleans#/overview", opened!.View.GetRawText(), StringComparison.Ordinal);

        // Switch section via pill button activation ("orleans:section:silos")
        await screen.Click("orleans:section:silos");
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
        await screen.Click("orleans:clear-filter");
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
    public async Task UiIntrospector_ExploresOrleansStateGraph_WithValidGraphvizDot()
    {
        using var person = EnterPerson();
        await using var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(TestBrain));

        var orleansGraph = await introspector.ExploreStateGraph(typeof(OrleansAppState).FullName!, maxStates: 64);
        Assert.True(orleansGraph.IsHealthy);
        Assert.True(orleansGraph.States.Length >= 4);
        Assert.Contains(orleansGraph.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_section"));
        Assert.Contains(orleansGraph.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_grain_filter"));
        Assert.Contains("digraph OrleansAppState", orleansGraph.Dot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossWindowOpenAppIntents_PlainInputsAndTypedQueryParameters_DeepLinkOrleans_AndEnforceDepartureRules()
    {
        using var person = EnterPerson();
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(OrleansApp).Assembly);

        var orleans = os.Get<IScreen>(new NeuronId(TestBrain, "app.orleans", "default"));
        _ = await orleans.Open();

        // 1. Plain input via os://window/open?app=orleans&input=EngineerFlow -> /orleans#/grains/EngineerFlow
        var orleansPlainIntent = AppSignals.OpenApp("orleans", "EngineerFlow");
        var orleansPlainQuery = ParseQuery(new Uri(orleansPlainIntent.Arg!).Query);
        await orleans.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(
                    FromNeuron: "infra-test/apps/engineer",
                    Transition: ViewTransition.FadeThrough,
                    Input: orleansPlainQuery["input"]),
                typeof(UiNavigateTo))));
        await os.Idle();
        var orleansWire = (await orleans.Open())!.View.GetRawText();
        Assert.Contains("/orleans#/grains/EngineerFlow", orleansWire, StringComparison.Ordinal);
        Assert.Contains("Grain: EngineerFlow", orleansWire, StringComparison.Ordinal);

        // Also verify typed OpenApp<BrowserNavArgs> & OrleansOpen(Input: ...) + Orleans departure lock guard
        var orleansTypedIntent = AppSignals.OpenApp(
            "orleans",
            new BrowserNavArgs(GrainFilter: "AgentNeuron"),
            transition: ViewTransition.SharedAxisZ,
            fromNeuron: "app.creator");
        var orleansTypedQuery = ParseQuery(new Uri(orleansTypedIntent.Arg!).Query);
        await orleans.Dispatch(new Command(
            ".",
            UiNavigateTo.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(
                    ArgsJson: orleansTypedQuery["args"],
                    FromNeuron: orleansTypedQuery["from"],
                    Transition: orleansTypedQuery["transition"]),
                typeof(UiNavigateTo))));
        await os.Idle();
        var orleansTypedWire = (await orleans.Open())!.View.GetRawText();
        Assert.Contains("/orleans#/grains/AgentNeuron", orleansTypedWire, StringComparison.Ordinal);
        Assert.Contains("\"transition\":\"shared-axis-z\"", orleansTypedWire, StringComparison.Ordinal);

        await orleans.Request(new OrleansOpen(Input: "EngineerFlow"));
        await os.Idle();
        Assert.Contains("/orleans#/grains/EngineerFlow", (await orleans.Open())!.View.GetRawText(), StringComparison.Ordinal);

        // No command pins the view (the method-name back door that reached SetNavigationLock is deleted), so the departure rule is judged on the state.
        var pinned = new OrleansAppState().SetNavigationLock(true);
        Assert.True(pinned.CanDepartRule(new NavigationDeparture(ToNeuron: "infra-test/apps/engineer")).Broken);
        Assert.False(pinned.CanDepartRule(new NavigationDeparture(ToNeuron: "infra-test/apps/engineer", Force: true)).Broken);

        await orleans.Dispatch(new Command(
            ".",
            UiNavigateFrom.SignalAlias,
            RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "infra-test/apps/engineer", Force: true),
                typeof(UiNavigateFrom))));
        await os.Idle();
        Assert.DoesNotContain("navigation_locked", (await orleans.Open())!.View.GetRawText(), StringComparison.Ordinal);
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
