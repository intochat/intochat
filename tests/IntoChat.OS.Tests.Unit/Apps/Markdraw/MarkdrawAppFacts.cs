using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Markdraw;

public sealed class MarkdrawAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> MarkdrawOperations
        => ScreenBaseline.Cases(
            typeof(MarkdrawApp),
            os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(MarkdrawOperations))]
    public Task MarkdrawBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task MarkdrawBddScenarioPasses()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(MarkdrawApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.markdraw", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task MarkdrawCoDrawingAndFilesVfsPersistencePreservesUserStrokesAndExports()
    {
        var ct = TestContext.Current.CancellationToken;
        var vfs = new WorkspaceFilesService();
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var markdraw = await os.Install<MarkdrawApp>();
        markdraw.App.FilesVfs = vfs;

        await markdraw.Request("open", new MarkdrawOpen("/work/architecture.markdraw"));
        Assert.Equal("/work/architecture.markdraw", markdraw.App.State.ActiveDocument);
        Assert.NotEmpty(markdraw.App.State.Elements);

        // Simulate user drawing a freehand stroke on the Markdraw canvas in the UI
        var userScene = markdraw.App.State.Content.Replace(
            "arrow data-engine -> markdraw-studio \"from-data\" color=#38bdf8",
            "arrow data-engine -> markdraw-studio \"from-data\" color=#38bdf8\nfreedraw id=user-sketch-1 color=#f59e0b",
            StringComparison.Ordinal);

        await markdraw.Request("sync-scene", new MarkdrawSyncScene(userScene, "/work/architecture.markdraw", "markdraw"));
        Assert.Contains(markdraw.App.State.Elements, e => e.Id == "user-sketch-1" && e.Kind == "freedraw");

        // Simulate Ino co-drawing on the same canvas via patch-elements
        await markdraw.Request("patch-elements", new MarkdrawPatchElements(
            Prompt: "Add \"Vector Store\" connected to data-engine",
            AddOrUpdateDsl: "rect \"Vector Store\" id=vector-store at 650,410 size 220x80 fill=#064e3b color=#10b981 rounded=10\narrow data-engine -> vector-store \"Embeddings\" color=#10b981",
            Document: "/work/architecture.markdraw"));

        // Both the user's freehand stroke AND Ino's patched node/arrow must coexist
        Assert.Contains(markdraw.App.State.Elements, e => e.Id == "user-sketch-1" && e.Kind == "freedraw");
        Assert.Contains(markdraw.App.State.Elements, e => e.Id == "vector-store" && e.Label == "Vector Store");
        Assert.Contains(markdraw.App.State.Elements, e => e.Kind == "arrow" && e.FromId == "data-engine" && e.ToId == "vector-store");

        var diskMarkdraw = await vfs.ReadTextAsync("/work/architecture.markdraw", ct);
        Assert.NotNull(diskMarkdraw);
        Assert.Contains("freedraw id=user-sketch-1", diskMarkdraw, StringComparison.Ordinal);
        Assert.Contains("id=vector-store", diskMarkdraw, StringComparison.Ordinal);

        // Export to .excalidraw JSON and .svg in Files VFS
        await markdraw.Request("export", new MarkdrawExport("excalidraw", "/work/architecture.excalidraw"));
        Assert.Equal("/work/architecture.excalidraw", markdraw.App.State.LastExportPath);
        var excalidrawJson = await vfs.ReadTextAsync("/work/architecture.excalidraw", ct);
        Assert.NotNull(excalidrawJson);
        Assert.Contains("\"type\": \"excalidraw\"", excalidrawJson, StringComparison.Ordinal);
        Assert.Contains("\"vector-store\"", excalidrawJson, StringComparison.Ordinal);

        await markdraw.Request("export", new MarkdrawExport("svg", "/work/architecture.svg"));
        Assert.Equal("/work/architecture.svg", markdraw.App.State.LastExportPath);
        var svgText = await vfs.ReadTextAsync("/work/architecture.svg", ct);
        Assert.NotNull(svgText);
        Assert.Contains("<svg", svgText, StringComparison.Ordinal);
        Assert.Contains("Vector Store", svgText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InoAndMarkdrawUnifiedDataAnalysisAndCoDrawingWorkflow()
    {
        var ct = TestContext.Current.CancellationToken;
        var vfs = new WorkspaceFilesService();
        await vfs.WriteTextAsync(
            "/work/q4_revenue.csv",
            """
            Region,Segment,Revenue
            Nordics,Enterprise,240000
            Nordics,MidMarket,110000
            DACH,Enterprise,185000
            DACH,SMB,65000
            UKI,Enterprise,140000
            """,
            ct);

        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        ino.App.FilesVfs = vfs;

        // 1. Query real CSV dataset from Files VFS with GROUP BY aggregation
        await ino.Request("query-db", new InoQueryDb("SELECT Region, Revenue FROM /work/q4_revenue.csv GROUP BY Region"));
        var tableCard = Assert.Single(ino.App.State.ActiveCards, c => c.Kind == "table");
        var chartCard = Assert.Single(ino.App.State.ActiveCards, c => c.Kind == "chart");
        Assert.Equal($"neuron://{vfs.BrainId}/local-disk.files/q4_revenue.csv", tableCard.SourceNeuronUri);
        Assert.NotNull(chartCard.ChartPoints);
        Assert.Equal(3, chartCard.ChartPoints!.Length);
        Assert.Equal("Nordics", chartCard.ChartPoints[0].Label);
        Assert.Equal(350000, chartCard.ChartPoints[0].Value);
    }

    [Fact]
    public void MarkdrawAppState_AggregateExplorer_UnfoldsHealthyStateGraphAndRuleGuards()
    {
        var graph = AggregateExplorer<MarkdrawAppState>.ExploreScreen<MarkdrawApp>(maxStates: 48).EnsureHealthy();

        Assert.True(graph.IsHealthy, string.Join("\n", graph.Violations));
        Assert.Empty(graph.Violations);
        Assert.Contains(graph.States, s => s.State.ActiveTool == "select");
        Assert.Contains(graph.States, s => s.State.ActiveTool == "rect");
        Assert.Contains(graph.States, s => s.State.ActiveTool == "diamond");
        Assert.Contains(graph.States, s => s.State.ActiveTool == "arrow");
        Assert.Contains(graph.States, s => s.State.ZoomPercent == 150);
        Assert.Contains(graph.States, s => s.State.ActiveLayer == 2);
        Assert.Contains(graph.States, s => s.State.ActiveDocument == "/work/pipeline-flow.markdraw");
        Assert.Contains(graph.States, s => s.State.SelectedElementId == "rect-api-gateway");

        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_tool", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("zoom_out_of_bounds", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("layer_out_of_bounds", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_shape_kind", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("empty_shape_label", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("unsupported_template", StringComparer.Ordinal));
        Assert.Contains(graph.RejectedTransitions, t => t.BrokenRules.Contains("invalid_export_format", StringComparer.Ordinal));

        Assert.Contains("digraph MarkdrawAppState", graph.ToDot(), StringComparison.Ordinal);
        Assert.Contains(MarkdrawApp.Bundle.Manifest.Operations, op => op.Name == UiCommand.SignalAlias);
    }

    [Fact]
    public async Task MarkdrawDomainRules_EnforceToolShapeEmptyDiagramZoomLayerAndExportGuards()
    {
        var initial = new MarkdrawAppState();

        var badTool = initial.SelectTool("laser");
        Assert.True(badTool.IsRefused);
        Assert.Contains("invalid_tool", badTool.Rejection!.Rules);

        var badShape = initial.AddShape("hexagon", "Node");
        Assert.True(badShape.IsRefused);
        Assert.Contains("invalid_shape_kind", badShape.Rejection!.Rules);

        var emptyLabel = initial.AddShape("rect", "   ");
        Assert.True(emptyLabel.IsRefused);
        Assert.Contains("empty_shape_label", emptyLabel.Rejection!.Rules);

        var addedShape = initial.AddShape("diamond", "Auth Gate");
        Assert.True(addedShape.IsAccepted);
        Assert.Contains(addedShape.Next!.Elements, e => e.Id == "diamond-auth-gate" && e.Kind == "diamond");

        var zoomLow = initial.SetZoom(10);
        Assert.True(zoomLow.IsRefused);
        Assert.Contains("zoom_out_of_bounds", zoomLow.Rejection!.Rules);

        var zoomHigh = initial.SetZoom(500);
        Assert.True(zoomHigh.IsRefused);
        Assert.Contains("zoom_out_of_bounds", zoomHigh.Rejection!.Rules);

        var layerLow = initial.SelectLayer(0);
        Assert.True(layerLow.IsRefused);
        Assert.Contains("layer_out_of_bounds", layerLow.Rejection!.Rules);

        var layerHigh = initial.SelectLayer(9);
        Assert.True(layerHigh.IsRefused);
        Assert.Contains("layer_out_of_bounds", layerHigh.Rejection!.Rules);

        var badTemplate = initial.SwitchTemplate("unknown");
        Assert.True(badTemplate.IsRefused);
        Assert.Contains("unsupported_template", badTemplate.Rejection!.Rules);

        var badExport = initial.RecordExport("bmp", "/work/out.bmp");
        Assert.True(badExport.IsRefused);
        Assert.Contains("invalid_export_format", badExport.Rejection!.Rules);

        var emptyCanvas = initial with { Elements = [] };
        var emptyExport = emptyCanvas.RecordExport("svg", "/work/out.svg");
        Assert.True(emptyExport.IsRefused);
        Assert.Contains("canvas_empty", emptyExport.Rejection!.Rules);

        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var markdraw = await os.Install<MarkdrawApp>();
        var baselineElementsCount = markdraw.App.State.Elements.Length;

        await markdraw.Request("sync-scene", new MarkdrawSyncScene("   ", "/work/architecture.markdraw", "markdraw"));
        Assert.NotNull(markdraw.App.LastRejection);
        Assert.Contains("empty_diagram_scene", markdraw.App.LastRejection!.Rules);
        Assert.Equal(baselineElementsCount, markdraw.App.State.Elements.Length);

        await markdraw.Request("export", new MarkdrawExport("bmp", "/work/architecture.bmp"));
        Assert.NotNull(markdraw.App.LastRejection);
        Assert.Contains("invalid_export_format", markdraw.App.LastRejection!.Rules);

        var renderedJson = System.Text.Json.JsonSerializer.Serialize(markdraw.App.Render());
        Assert.Contains("Canvas rule refused command", renderedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdrawApp_UiIntrospector_UnfoldsCleanAccordantStateGraphAndDot()
    {
        var brain = new BrainId("markdraw-introspect");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", brain));
        await using var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);
        os.Register<AgentContextNeuron>();
        os.Register<UiIntrospectorNeuron>();

        var introspector = os.Get<IUiIntrospector>(IUiIntrospector.NeuronIdOf(brain));
        var snapshot = await introspector.ExploreStateGraph(typeof(MarkdrawApp).FullName!, maxStates: 48);

        Assert.True(snapshot.IsHealthy, string.Join("\n", snapshot.Violations));
        Assert.Empty(snapshot.Violations);
        Assert.True(snapshot.States.Length >= 8);
        Assert.Contains(snapshot.Transitions, t => t.Accepted);
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("invalid_tool", StringComparer.Ordinal));
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("zoom_out_of_bounds", StringComparer.Ordinal));
        Assert.Contains(snapshot.Transitions, t => !t.Accepted && t.BrokenRules.Contains("layer_out_of_bounds", StringComparer.Ordinal));
        Assert.Contains("digraph MarkdrawAppState", snapshot.Dot, StringComparison.Ordinal);
    }
}
