using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.ArtCraft;
using IntoChat.OS.Apps.Calculator;
using IntoChat.OS.Apps.Data;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Usage;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;

namespace IntoChat.OS.Tests.Unit.UI;

public sealed class StudioAndCraftReactorFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CalculatorAggregateExplorerVerifiesInvariantsTransitionsAndClearGuard()
    {
        var graph = AggregateExplorer<CalculatorAppState>.ExploreScreen<CalculatorApp>(maxStates: 32);
        graph.EnsureHealthy();

        Assert.True(graph.IsHealthy);
        Assert.NotEmpty(graph.AcceptedTransitions);
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("display_already_empty"));
    }

    [Fact]
    public void DataStudioAggregateExplorerVerifiesChartKindsAndEmptyDatasetExportGuards()
    {
        var graph = AggregateExplorer<DataStudioAppState>.ExploreScreen<DataStudioApp>(maxStates: 24);
        graph.EnsureHealthy();

        Assert.True(graph.IsHealthy);
        Assert.Contains(graph.AcceptedTransitions, t => t.Command.Contains("SwitchChart(\"line\")", StringComparison.Ordinal));
        Assert.Contains(graph.AcceptedTransitions, t => t.Command.Contains("SwitchChart(\"pie\")", StringComparison.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_chart_kind"));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("dataset_empty"));
    }

    [Fact]
    public void UsageAggregateExplorerVerifiesBudgetCapInvariantsAndRefusesNonPositiveBudget()
    {
        var graph = AggregateExplorer<UsageAppState>.ExploreScreen<UsageApp>(maxStates: 16);
        graph.EnsureHealthy();

        Assert.True(graph.IsHealthy);
        Assert.Contains(graph.AcceptedTransitions, t => t.Command.Contains("ApplyBudgetCap(10000000)", StringComparison.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("non_positive_budget"));
    }

    [Fact]
    public void FriendsAggregateExplorerVerifiesFeedScopeTabsAndRefusesInvalidScope()
    {
        var graph = AggregateExplorer<FriendsAppState>.ExploreScreen<FriendsApp>(maxStates: 16);
        graph.EnsureHealthy();

        Assert.True(graph.IsHealthy);
        Assert.Contains(graph.AcceptedTransitions, t => t.Command.Contains("SelectScope(\"public\")", StringComparison.Ordinal));
        Assert.Contains(graph.AcceptedTransitions, t => t.Command.Contains("SelectScope(\"friends\")", StringComparison.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_scope"));
    }

    [Fact]
    public void ArtCraftPhotoCraftFilmCraftAndMarkdrawExplorersVerifyAllCreativeAggregates()
    {
        var artGraph = AggregateExplorer<ArtCraftStudioAppState>.ExploreScreen<ArtCraftApp>(maxStates: 16).EnsureHealthy();
        Assert.Contains(artGraph.AcceptedTransitions, t => t.Command.Contains("SwitchExecutionMode(\"mcp\")", StringComparison.Ordinal));
        Assert.Contains(artGraph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_execution_mode"));

        var photoGraph = AggregateExplorer<PhotoCraftAppState>.ExploreScreen<PhotoCraftApp>(maxStates: 24).EnsureHealthy();
        Assert.Contains(photoGraph.AcceptedTransitions, t => t.Command.Contains("ToggleLayerByIndex(0)", StringComparison.Ordinal));
        Assert.Contains(photoGraph.RejectedTransitions, t => t.BrokenRules.Contains("layer_out_of_range"));
        Assert.Contains(photoGraph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_execution_mode"));

        var filmGraph = AggregateExplorer<FilmCraftAppState>.ExploreScreen<FilmCraftApp>(maxStates: 16).EnsureHealthy();
        Assert.Contains(filmGraph.AcceptedTransitions, t => t.Command.Contains("SwitchExecutionMode(\"native-linux\")", StringComparison.Ordinal));
        Assert.Contains(filmGraph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_execution_mode"));

        var markdrawGraph = AggregateExplorer<MarkdrawAppState>.ExploreScreen<MarkdrawApp>(maxStates: 24).EnsureHealthy();
        Assert.Contains(markdrawGraph.AcceptedTransitions, t => t.Command.Contains("SwitchTemplate(\"pipeline\")", StringComparison.Ordinal));
        Assert.Contains(markdrawGraph.RejectedTransitions, t => t.BrokenRules.Contains("unsupported_template"));
        Assert.Contains(markdrawGraph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_export_format"));
    }

    [Fact]
    public void All15ArtCraftSuiteAppsRenderCleanStudioAppShellDefaultingToNativeMcpAndSupportingExplicitWasmMode()
    {
        static ArtCraftStudioAppBase CreateSuiteApp<TApp>() where TApp : ArtCraftStudioAppBase, new()
            => Activator.CreateInstance<TApp>();

        ArtCraftStudioAppBase[] suite =
        [
            CreateSuiteApp<ArtCraftApp>(), CreateSuiteApp<VectorCraftApp>(), CreateSuiteApp<LightCraftApp>(), CreateSuiteApp<EffectCraftApp>(), CreateSuiteApp<DesignCraftApp>(),
            CreateSuiteApp<PdfCraftApp>(), CreateSuiteApp<WordCraftApp>(), CreateSuiteApp<SoundCraftApp>(), CreateSuiteApp<DeckCraftApp>(), CreateSuiteApp<CadCraftApp>(),
            CreateSuiteApp<GridCraftApp>(), CreateSuiteApp<FigCraftApp>(), CreateSuiteApp<TextureCraftApp>(), CreateSuiteApp<MaterialCraftApp>(), CreateSuiteApp<SolidCraftApp>(),
        ];

        foreach (var app in suite)
        {
            var initialState = ArtCraftStudioAppState.For(app.Descriptor.Id);
            Assert.Equal("wasm-ui", initialState.ExecutionMode);

            var wasmView = app.PreviewView(initialState);
            Assert.Equal(app.Descriptor.Title, wasmView.Title);
            var wasmJson = JsonSerializer.Serialize(wasmView, RuntimeProtocol.OptionsFor(typeof(View)));
            Assert.Contains("\"kind\":\"webBrowser\"", wasmJson, StringComparison.Ordinal);

            var mcpView = app.PreviewView(initialState with { ExecutionMode = "mcp" });
            Assert.Equal(app.Descriptor.Title, mcpView.Title);
            var json = JsonSerializer.Serialize(mcpView, RuntimeProtocol.OptionsFor(typeof(View)));
            Assert.Contains("\"kind\":\"appShell\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("data:image/png;base64,", json, StringComparison.Ordinal);
            Assert.DoesNotContain("-cli", json, StringComparison.Ordinal);
            Assert.DoesNotContain("1808", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"kind\":\"webBrowser\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"surfaceKind\":\"acrylic\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CalculatorDataStudioPhotoCraftAndMarkdrawInteractiveKitCommandsWorkEndToEnd()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(CalculatorApp).Assembly);

        var rejectedHeard = new List<Rejected>();
        await using var sub = await os.ClusterListeners.Listen<Rejected>(
            "w2-studio-rejected-listener",
            (_, signal, _) =>
            {
                rejectedHeard.Add(signal);
                return Task.CompletedTask;
            });

        var brain = new BrainId("test");

        // 1. Calculator: guard disables Clear on empty display; keypad buttons compute 7 + 2 = 9
        var calcId = new NeuronId(brain, "app.calculator", "main");
        var calc = os.Get<IScreen>(calcId);
        var c0 = (await calc.Open())!;
        Assert.False(FindNodeByKey(c0.View, "clear-display").GetProperty("enabled").GetBoolean());

        await calc.Click("clear-display");
        await os.Idle();
        Assert.Contains(rejectedHeard, r => r.Rules.Contains("display_already_empty"));

        var c1 = (await calc.Open())!;
        await calc.Click("key-0");  // 7
        await os.Idle();
        var c2 = (await calc.Open())!;
        await calc.Click("key-15"); // +
        await os.Idle();
        var c3 = (await calc.Open())!;
        await calc.Click("key-9");  // 2
        await os.Idle();
        var c4 = (await calc.Open())!;
        await calc.Click("key-14"); // =
        await os.Idle();

        var c5 = (await calc.Open())!;
        Assert.True(FindNodeByKey(c5.View, "clear-display").GetProperty("enabled").GetBoolean());
        var calcState = (await ((IBrainObserver)os).State(calcId, Ct))!.Value
            .Deserialize<CalculatorAppState>(RuntimeProtocol.OptionsFor(typeof(CalculatorAppState)))!;
        Assert.Equal("9", calcState.Expression);
        Assert.Contains("7+2 = 9", calcState.History!);

        // 2. DataStudio: export buttons guarded when empty; SwitchChart("line") activates cleanly; no Mono SQL text
        var dataId = new NeuronId(brain, "app.data", "main");
        var data = os.Get<IScreen>(dataId);
        var d0 = (await data.Open())!;
        Assert.False(FindNodeByKey(d0.View, "export-csv").GetProperty("enabled").GetBoolean());
        Assert.False(FindNodeByKey(d0.View, "diagram-in-markdraw").GetProperty("enabled").GetBoolean());

        await data.Click("line");
        await os.Idle();
        var dataState = (await ((IBrainObserver)os).State(dataId, Ct))!.Value
            .Deserialize<DataStudioAppState>(RuntimeProtocol.OptionsFor(typeof(DataStudioAppState)))!;
        Assert.Equal("line", dataState.ChartKind);

        var sampleRendered = DataStudioViewRenderer.Render(dataState with { ActiveQuery = "SELECT Stage, SUM(Amount) FROM deals" });
        var sampleJson = JsonSerializer.Serialize(sampleRendered, RuntimeProtocol.OptionsFor(typeof(View)));
        Assert.DoesNotContain("\"mono\":true", sampleJson, StringComparison.Ordinal);

        // 3. PhotoCraft & Markdraw: direct DDD UI button activations update aggregate state
        var photoId = new NeuronId(brain, "app.photocraft", "main");
        var photo = os.Get<IScreen>(photoId);
        var p0 = (await photo.Open())!;
        await photo.Click("mode-mcp");
        await os.Idle();
        var p1 = (await photo.Open())!;
        await photo.Click("toggle-layer-0");
        await os.Idle();
        var photoState = (await ((IBrainObserver)os).State(photoId, Ct))!.Value
            .Deserialize<PhotoCraftAppState>(RuntimeProtocol.OptionsFor(typeof(PhotoCraftAppState)))!;
        Assert.Contains(0, photoState.HiddenLayerIndices!);

        var mdId = new NeuronId(brain, "app.markdraw", "main");
        var md = os.Get<IScreen>(mdId);
        var m0 = (await md.Open())!;
        await md.Click("tpl-pipeline");
        await os.Idle();
        var mdState = (await ((IBrainObserver)os).State(mdId, Ct))!.Value
            .Deserialize<MarkdrawAppState>(RuntimeProtocol.OptionsFor(typeof(MarkdrawAppState)))!;
        Assert.Equal("/work/pipeline-flow.markdraw", mdState.ActiveDocument);
        Assert.Equal(2, mdState.Revision);
    }

    private static JsonElement FindNodeByKey(JsonElement root, string key)
    {
        var stack = new Stack<JsonElement>([root]);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.ValueKind == JsonValueKind.Object)
            {
                if (cur.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key)
                {
                    return cur;
                }

                foreach (var prop in cur.EnumerateObject())
                {
                    if (prop.Name is not ("emit" or "args"))
                    {
                        stack.Push(prop.Value);
                    }
                }
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cur.EnumerateArray())
                {
                    stack.Push(item);
                }
            }
        }

        throw new KeyNotFoundException($"Node with key '{key}' not found in view JSON.");
    }
}
