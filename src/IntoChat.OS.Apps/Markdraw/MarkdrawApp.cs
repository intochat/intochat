/*
# Markdraw
Role: Default
Shipped: true
Operations: open, read-canvas, sync-scene, patch-elements, from-data, save, export

Excalidraw-inspired interactive diagram, architecture, and data-flow canvas studio powered by the Flutter markdraw package.
Persists .markdraw Markdown sketch files, .excalidraw JSON scenes, and .svg exports directly to IFilesVfsService (/work/*),
and exposes co-drawing operations (read-canvas, patch-elements, from-data, sync-scene) so both the user and Ino can inspect
and mutate the exact same live canvas without overwriting hand-drawn strokes.
*/
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Markdraw;

[Signal("markdraw.open")]
public sealed record MarkdrawOpen(string? Document = null, string? Template = null) : Signal;

[Signal("markdraw.read-canvas")]
public sealed record MarkdrawReadCanvas(string? Document = null) : Signal;

[Signal("markdraw.sync-scene")]
public sealed record MarkdrawSyncScene(string? Content = null, string? Document = null, string? Format = null) : Signal;

[Signal("markdraw.patch-elements")]
public sealed record MarkdrawPatchElements(string? Prompt = null, string? AddOrUpdateDsl = null, string? RemoveIdsCsv = null, string? Document = null) : Signal;

[Signal("markdraw.from-data")]
public sealed record MarkdrawFromData(string? Title = null, string? ColumnsCsv = null, string? RowsSummary = null, string? SourceUri = null, string? Document = null) : Signal;

[Signal("markdraw.save")]
public sealed record MarkdrawSave(string? Document = null, string? Content = null) : Signal;

[Signal("markdraw.export")]
public sealed record MarkdrawExport(string? Format = null, string? TargetPath = null) : Signal;

[GenerateSerializer, Alias("markdraw.element-summary")]
public sealed record MarkdrawElementSummary(
    [property: Id(0)] string Id,
    [property: Id(1)] string Kind,
    [property: Id(2)] string Label,
    [property: Id(3)] int X,
    [property: Id(4)] int Y,
    [property: Id(5)] int Width,
    [property: Id(6)] int Height,
    [property: Id(7)] string? FromId = null,
    [property: Id(8)] string? ToId = null,
    [property: Id(9)] string? Color = null);

public sealed record MarkdrawAppState(
    string ActiveDocument,
    string Format,
    string Content,
    int Revision,
    string Status,
    MarkdrawElementSummary[] Elements,
    string[] WorkspaceDrawings,
    string? LastExportPath = null,
    string? LastMutationSummary = null)
{
    public MarkdrawAppState() : this(
        "/work/architecture.markdraw", "markdraw", MarkdrawApp.DefaultArchitectureScene, 1,
        "Markdraw Canvas Ready · Co-drawing enabled with Ino & Files VFS",
        MarkdrawSceneEngine.ParseSceneElements(MarkdrawApp.DefaultArchitectureScene),
        ["/work/architecture.markdraw", "/work/pipeline-flow.markdraw"])
    { }

    [Invariant("Canvas revision is strictly positive."), JsonIgnore]
    public bool PositiveRevision => Revision >= 1;

    [Invariant("Scene format is markdraw or excalidraw."), JsonIgnore]
    public bool ValidSceneFormat => Format is "markdraw" or "excalidraw";

    [Invariant("ActiveDocument and scene collections are non-null."), JsonIgnore]
    public bool NonNullScene => !string.IsNullOrWhiteSpace(ActiveDocument) && Elements is not null && WorkspaceDrawings is not null;

    public Rule HasCanvasElements() => Decision.Rule("canvas_empty", Elements, static e => e.Length == 0, "Add at least one element before exporting.");

    public Rule SupportedExportFormat(string? format) => Decision.Rule(
        "invalid_export_format",
        format,
        static f => string.IsNullOrWhiteSpace(f) || f.Trim().TrimStart('.').ToLowerInvariant() is not ("excalidraw" or "svg" or "markdraw"),
        "Export format must be excalidraw, svg, or markdraw.");

    public Decision<MarkdrawAppState> SwitchTemplate([Sample("architecture", "pipeline", "unknown")] string template)
    {
        var clean = template?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Decision.Check("switch-template", Decision.Rule("unsupported_template", clean, static t => t is not ("architecture" or "pipeline" or "crm" or "sales"), "Template must be architecture or pipeline.")) is { } refusal)
        {
            return refusal;
        }

        var isPipeline = clean is "pipeline" or "crm" or "sales";
        var doc = isPipeline ? "/work/pipeline-flow.markdraw" : "/work/architecture.markdraw";
        var content = isPipeline ? MarkdrawApp.DefaultPipelineScene : MarkdrawApp.DefaultArchitectureScene;
        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        var nextRev = Revision + 1;
        return Decision.Accept(this with { ActiveDocument = doc, Format = "markdraw", Content = content, Revision = nextRev, Elements = elements, Status = $"Opened {doc} ({elements.Length} canvas element(s), rev {nextRev})" });
    }

    public Decision<MarkdrawAppState> RecordExport([Sample("excalidraw", "svg", "bmp")] string format, [Sample("/work/architecture.excalidraw")] string targetPath)
    {
        if (Decision.Check("export", HasCanvasElements(), SupportedExportFormat(format), Decision.Rule("empty_export_path", targetPath, static p => string.IsNullOrWhiteSpace(p), "Target export path is required.")) is { } refusal)
        {
            return refusal;
        }

        var cleanFormat = format.Trim().TrimStart('.').ToLowerInvariant();
        return Decision.Accept(this with { LastExportPath = targetPath.Trim(), Status = $"Exported {Elements.Length} element(s) to {targetPath.Trim()} ({cleanFormat})" });
    }
}

[Neuron("app.markdraw"), Screen("app.markdraw", Title = "Markdraw")]
public sealed class MarkdrawApp : AggregateScreen<MarkdrawAppState>, IWithFilesVfs
{
    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        return base.OnActivate(cancellationToken);
    }

    public const string DefaultArchitectureScene =
        "# IntoChat Neuron & Data Architecture\n\n" +
        "Interactive `.markdraw` canvas shared between you and **Ino**. Draw freely on the canvas or ask Ino to add nodes, connect arrows, or diagram any dataset.\n\n" +
        "```markdraw\n" +
        "@background #0f172a\n" +
        "@grid 20\n\n" +
        "frame \"DigitalBrain OS · Live Co-Drawing Canvas\" id=os-frame at 40,40 size 880x440\n" +
        "rect \"Ino Assistant (apps/ino)\" id=ino-agent at 80,110 size 220x80 fill=#1e1b4b color=#818cf8 rounded=12\n" +
        "rect \"Files VFS (/work/*)\" id=files-vfs at 370,110 size 210x80 fill=#064e3b color=#34d399 rounded=12\n" +
        "rect \"Data Analytics Engine\" id=data-engine at 650,110 size 220x80 fill=#1e3a8a color=#60a5fa rounded=12\n" +
        "rect \"Markdraw Studio (apps/markdraw)\" id=markdraw-studio at 220,280 size 240x84 fill=#065f46 color=#10b981 rounded=12\n" +
        "diamond \"Interactive Charts & Tables\" id=chart-view at 560,280 size 230x84 fill=#3b0764 color=#c084fc\n\n" +
        "arrow ino-agent -> files-vfs \"Read / Write VFS\" color=#34d399\n" +
        "arrow files-vfs -> data-engine \"CSV / JSON / SQL\" color=#60a5fa\n" +
        "arrow ino-agent -> markdraw-studio \"patch-elements\" color=#10b981\n" +
        "arrow data-engine -> chart-view \"Visualize\" color=#c084fc\n" +
        "arrow data-engine -> markdraw-studio \"from-data\" color=#38bdf8\n" +
        "```";

    public const string DefaultPipelineScene = """
        # IntoChat Data & Deal Pipeline Flow

        ```markdraw
        @background #0f172a
        @grid 20

        rect "CRM Opportunities (5 deals)" id=crm-source at 80,120 size 220x80 fill=#1e3a8a color=#60a5fa rounded=10
        diamond "Stage Filter (Proposal / Contract)" id=stage-filter at 370,115 size 220x90 fill=#422006 color=#f59e0b
        rect "Enterprise Pilot (€240,000)" id=deal-enterprise-pilot at 660,70 size 220x70 fill=#064e3b color=#34d399 rounded=10
        rect "Depot Software (€186,000)" id=deal-depot at 660,175 size 220x70 fill=#1e1b4b color=#818cf8 rounded=10
        rect "Executive Chart & Brief" id=exec-brief at 370,290 size 230x76 fill=#3b0764 color=#c084fc rounded=10

        arrow crm-source -> stage-filter "SQL / CSV Query" color=#60a5fa
        arrow stage-filter -> deal-enterprise-pilot "Top 1" color=#34d399
        arrow stage-filter -> deal-depot "Top 2" color=#818cf8
        arrow stage-filter -> exec-brief "Aggregate €627.7k" color=#c084fc
        ```
        """;

    public IFilesVfsService? FilesVfs { get; set; }
    public const string AppName = "markdraw";

    public static AppBundle Bundle { get; } = new(
        "markdraw", "1.0.0",
        new AppManifest(
            Title: "Markdraw",
            Description: "Excalidraw-inspired diagram, whiteboard, and architecture studio with .markdraw Markdown sketch blocks, .excalidraw JSON interop, Files VFS persistence, and shared Ino co-drawing.",
            Operations:
            [
                new("open", "Open a .markdraw or .excalidraw file from Files VFS or initialize an architecture/data-flow template."),
                new("read-canvas", "Inspect all shapes, nodes, text labels, and arrows currently on the active Markdraw canvas."),
                new("sync-scene", "Synchronize interactive user canvas edits from the Flutter MarkdrawEditor widget and persist to Files VFS."),
                new("patch-elements", "Co-draw on the active canvas by adding, updating, or connecting nodes and arrows without losing user strokes."),
                new("from-data", "Generate or update an interactive data-flow or schema diagram on the canvas from a dataset or SQL table."),
                new("save", "Save the active .markdraw or .excalidraw document to the Files workspace (/work/*)."),
                new("export", "Export the active Markdraw scene to .excalidraw JSON or .svg vector format in Files VFS."),
            ],
            Settings: [], Slots: [], Icon: "icon.svg", Dock: 4, Studio: true,
            Toolbar: [new("open", Icon: "pen-tool", Label: "Open Canvas"), new("save", Icon: "save", Label: "Save to Files"), new("export", Icon: "download", Label: "Export .excalidraw")]),
        "", new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(MarkdrawApp).AssemblyQualifiedName,
    };

    public new MarkdrawAppState State => base.State;
    public View Render() => PreviewView(State);

    [On]
    public async Task Open(MarkdrawOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var content = !string.IsNullOrWhiteSpace(request.Template) ? SelectTemplateContent(request.Template, doc) : State.Content;
        var drawings = State.WorkspaceDrawings;

        if (FilesVfs is not null)
        {
            try
            {
                await EnsureDefaultDrawingsSeededAsync();
                if (string.IsNullOrWhiteSpace(request.Template) && await FilesVfs.ReadTextAsync(doc) is { Length: > 0 } existingText) { content = existingText; }
                else { doc = (await FilesVfs.WriteTextAsync(doc, content)).VfsPath; }
                drawings = await ListWorkspaceDrawingsAsync(doc);
            }
            catch (Exception ex) { OsConsoleLog.For<MarkdrawApp>().LogSwallowed(ex, "Failed to initialize default templates in VFS."); }
        }

        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        await Show(State with { ActiveDocument = doc, Format = InferFormat(doc, content), Content = content, Elements = elements, WorkspaceDrawings = drawings, Status = $"Opened {doc} ({elements.Length} canvas element(s), rev {State.Revision})" });
    }

    [On]
    public async Task ReadCanvas(MarkdrawReadCanvas request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var content = await ReadVfsOrCurrentContentAsync(doc);
        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        await Show(State with { ActiveDocument = doc, Content = content, Elements = elements, Status = $"Read {elements.Length} element(s) from {doc} (rev {State.Revision})" });
    }

    [On]
    public async Task SyncScene(MarkdrawSyncScene request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var rawContent = string.IsNullOrWhiteSpace(request.Content) ? State.Content : UnescapeIncomingScene(request.Content);
        var format = NormalizeSceneFormat(request.Format, doc, rawContent);
        var (savedDoc, drawings) = await PersistSceneAsync(doc, rawContent);
        var elements = MarkdrawSceneEngine.ParseSceneElements(rawContent);
        var nextRev = State.Revision + 1;
        await Show(State with
        {
            ActiveDocument = savedDoc,
            Format = format,
            Content = rawContent,
            Revision = nextRev,
            Elements = elements,
            WorkspaceDrawings = drawings,
            LastMutationSummary = $"Synced live canvas to {savedDoc} ({elements.Length} element(s), rev {nextRev})",
            Status = $"Synced {savedDoc} · {elements.Length} element(s) · rev {nextRev}",
        });
    }

    [On]
    public async Task PatchElements(MarkdrawPatchElements request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var (mergedContent, mutationSummary) = MarkdrawSceneEngine.ApplyCoDrawingPatch(await ReadVfsOrCurrentContentAsync(doc), request.Prompt, request.AddOrUpdateDsl, request.RemoveIdsCsv);
        var (savedDoc, drawings) = await PersistSceneAsync(doc, mergedContent);
        var elements = MarkdrawSceneEngine.ParseSceneElements(mergedContent);
        var nextRev = State.Revision + 1;
        await Show(State with
        {
            ActiveDocument = savedDoc,
            Format = InferFormat(savedDoc, mergedContent),
            Content = mergedContent,
            Revision = nextRev,
            Elements = elements,
            WorkspaceDrawings = drawings,
            LastMutationSummary = mutationSummary,
            Status = $"{mutationSummary} · Saved to {savedDoc} (rev {nextRev})",
        });
    }

    [On]
    public async Task FromData(MarkdrawFromData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Pipeline Opportunities by Amount" : request.Title.Trim();
        var doc = string.IsNullOrWhiteSpace(request.Document) ? "/work/data-lineage.markdraw" : NormalizeDocumentPath(request.Document);
        var cols = (request.ColumnsCsv ?? "Opportunity, Account, Stage, Amount").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rowsSummary = string.IsNullOrWhiteSpace(request.RowsSummary) ? "5 rows · Total €627,700 · Top: Enterprise pilot (€240,000)" : request.RowsSummary.Trim();
        var sourceUri = string.IsNullOrWhiteSpace(request.SourceUri) ? "neuron://local/postgres/main" : request.SourceUri.Trim();

        var generated = MarkdrawSceneEngine.BuildDataLineageMarkdraw(title, cols, rowsSummary, sourceUri);
        var (savedDoc, drawings) = await PersistSceneAsync(doc, generated);
        var elements = MarkdrawSceneEngine.ParseSceneElements(generated);
        var nextRev = State.Revision + 1;
        var summary = $"Generated data-flow diagram '{title}' ({elements.Length} elements) in {savedDoc}";
        await Show(State with { ActiveDocument = savedDoc, Format = "markdraw", Content = generated, Revision = nextRev, Elements = elements, WorkspaceDrawings = drawings, LastMutationSummary = summary, Status = $"{summary} (rev {nextRev})" });
    }

    [On]
    public async Task Save(MarkdrawSave request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var content = string.IsNullOrWhiteSpace(request.Content) ? State.Content : UnescapeIncomingScene(request.Content);
        var (savedDoc, drawings) = await PersistSceneAsync(doc, content);
        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        await Show(State with { ActiveDocument = savedDoc, Format = InferFormat(savedDoc, content), Content = content, Elements = elements, WorkspaceDrawings = drawings, Status = $"Saved {savedDoc} ({elements.Length} element(s), rev {State.Revision})" });
    }

    [On]
    public async Task Export(MarkdrawExport request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawFormat = string.IsNullOrWhiteSpace(request.Format) ? "excalidraw" : request.Format.Trim().TrimStart('.').ToLowerInvariant();
        var format = rawFormat is "excalidraw" or "svg" or "markdraw" ? rawFormat : "excalidraw";
        var stem = Path.GetFileNameWithoutExtension(State.ActiveDocument);
        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath) ? $"/work/{(string.IsNullOrWhiteSpace(stem) ? "architecture" : stem)}.{format}" : NormalizeExportPath(request.TargetPath, format);
        var payload = format switch { "svg" => MarkdrawSceneEngine.RenderSvgPreview(State.Elements), "markdraw" => State.Content, _ => MarkdrawSceneEngine.RenderExcalidrawJson(State.Elements) };

        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                targetPath = (await FilesVfs.WriteTextAsync(targetPath, payload)).VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(State.ActiveDocument);
            }
            catch (Exception ex) { OsConsoleLog.For<MarkdrawApp>().LogSwallowed(ex, "Failed to export canvas to VFS."); }
        }

        await Show(State with { LastExportPath = targetPath, WorkspaceDrawings = drawings, Status = $"Exported {State.Elements.Length} element(s) to {targetPath} ({format})" });
    }

    public Task SaveActiveSceneAsync() => Save(new MarkdrawSave(State.ActiveDocument));
    public Task ExportSceneAsync(string format) => Export(new MarkdrawExport(format));
    public Task DiagramCrmDataAsync() => FromData(new MarkdrawFromData("Pipeline Opportunities by Amount"));

    protected override Node Render(MarkdrawAppState state)
    {
        var shapeCount = state.Elements.Count(e => e.Kind is not ("arrow" or "line"));
        var connectorCount = state.Elements.Count(e => e.Kind is "arrow" or "line");

        return VStack(Space.Md)[
            HeaderBar(
                $"Markdraw · {Path.GetFileName(state.ActiveDocument)}",
                state.Status,
                StatusPill(StatusKind.Done, $"{state.Format} · rev {state.Revision}"),
                Button("Export .excalidraw", "file-json").Key("export-excalidraw").Small().OnClick(() => ExportSceneAsync("excalidraw")).Guard(state.HasCanvasElements()),
                Button("Export .svg", "image").Key("export-svg").Small().OnClick(() => ExportSceneAsync("svg")).Guard(state.HasCanvasElements()),
                Button("Diagram CRM Data", "bar-chart-2").Key("diagram-crm-data").Small().OnClick(() => DiagramCrmDataAsync()),
                PrimaryButton("Save to Files", "save").Key("save-to-files").Small().OnClick(() => SaveActiveSceneAsync())),
            ResponsiveSplit(
                leading: VStack(Space.Sm)[
                    Grid(2, Space.Sm)[
                        StatCard("kpi-markdraw-shapes", "Shapes", $"{shapeCount}", state.Format, BadgeTone.Accent, "box"),
                        StatCard("kpi-markdraw-links", "Arrows", $"{connectorCount}", $"rev {state.Revision}", BadgeTone.Done, "git-commit")
                    ],
                    Card("Templates & Drawings", $"{state.WorkspaceDrawings.Length} workspace file(s)")[
                        VStack(Space.Xs,
                        [
                            HStack(Space.Xs)[
                                GhostButton("Architecture").Key("tpl-architecture").Small().OnClick<MarkdrawAppState>(x => x.SwitchTemplate("architecture")),
                                GhostButton("Pipeline").Key("tpl-pipeline").Small().OnClick<MarkdrawAppState>(x => x.SwitchTemplate("pipeline"))
                            ],
                            .. state.WorkspaceDrawings.Select(path => MutedText(path).Tabular().ToNode()),
                        ])
                    ],
                    !string.IsNullOrWhiteSpace(state.LastExportPath) ? Card("Last Export", state.LastExportPath)[MutedText("Persisted to Files VFS")].ToNode() : null
                ],
                trailing: VStack(Space.Md)[
                    MarkdrawSceneEngine.BuildMarkdrawCanvasCard(
                        $"markdraw-canvas-{state.Revision}", $"Markdraw Canvas · {Path.GetFileName(state.ActiveDocument)}",
                        state.ActiveDocument, state.Format, state.Content, state.Revision, state.Status,
                        state.Elements, state.WorkspaceDrawings, state.LastExportPath),
                    Table(
                        $"Canvas Elements Inventory ({state.Elements.Length} items in {state.ActiveDocument})",
                        ["ID", "Kind", "Label / Connection", "Geometry"],
                        state.Elements.Select(e => (IReadOnlyList<string>)
                        [
                            e.Id, e.Kind,
                            e.Kind is "arrow" or "line" && !string.IsNullOrWhiteSpace(e.FromId)
                                ? $"{e.FromId} -> {e.ToId}" + (string.IsNullOrWhiteSpace(e.Label) ? string.Empty : $" ({e.Label})")
                                : e.Label,
                            e.Kind is "arrow" or "line" ? "connector" : $"{e.X},{e.Y} ({e.Width}x{e.Height})",
                        ]).ToArray(),
                        sourceNeuronUri: "neuron://local/apps/markdraw#elements") with
                    { From = "Markdraw" }
                ],
                leadingExtent: 260)
        ];
    }

    private string ResolveTargetDoc(string? requestedDoc) =>
        string.IsNullOrWhiteSpace(requestedDoc)
            ? (string.IsNullOrWhiteSpace(State.ActiveDocument) ? "/work/architecture.markdraw" : State.ActiveDocument)
            : NormalizeDocumentPath(requestedDoc);

    private static string SelectTemplateContent(string template, string targetDoc) =>
        template.Trim().ToLowerInvariant() is "pipeline" or "crm" or "sales" || targetDoc.Contains("pipeline", StringComparison.OrdinalIgnoreCase)
            ? DefaultPipelineScene
            : DefaultArchitectureScene;

    private async Task EnsureDefaultDrawingsSeededAsync()
    {
        if (FilesVfs is null) { return; }
        try
        {
            if (await FilesVfs.ResolveFileAsync("/work/architecture.markdraw") is null) { await FilesVfs.WriteTextAsync("/work/architecture.markdraw", DefaultArchitectureScene); }
            if (await FilesVfs.ResolveFileAsync("/work/pipeline-flow.markdraw") is null) { await FilesVfs.WriteTextAsync("/work/pipeline-flow.markdraw", DefaultPipelineScene); }
        }
        catch (Exception ex) { OsConsoleLog.For<MarkdrawApp>().LogSwallowed(ex, "Failed to seed demo drawings into VFS."); }
    }

    private async Task<string> ReadVfsOrCurrentContentAsync(string docPath)
    {
        if (FilesVfs is not null)
        {
            try { if (await FilesVfs.ReadTextAsync(docPath) is { Length: > 0 } text) { return text; } }
            catch (Exception ex) { OsConsoleLog.For<MarkdrawApp>().LogSwallowed(ex, "Failed to read drawing document from VFS."); }
        }
        return State.Content;
    }

    private async Task<(string SavedPath, string[] Drawings)> PersistSceneAsync(string docPath, string content)
    {
        var saved = docPath;
        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                saved = (await FilesVfs.WriteTextAsync(docPath, content)).VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(saved);
            }
            catch (Exception ex) { OsConsoleLog.For<MarkdrawApp>().LogSwallowed(ex, "Failed to persist canvas scene to VFS."); }
        }
        return (saved, drawings);
    }

    private async Task<string[]> ListWorkspaceDrawingsAsync(string activePath)
    {
        var results = string.IsNullOrWhiteSpace(activePath) ? new List<string>() : [activePath];
        if (FilesVfs is not null)
        {
            try
            {
                results.AddRange((await FilesVfs.ListFilesAsync())
                    .Where(f => Path.GetExtension(f.Name).ToLowerInvariant() is ".markdraw" or ".excalidraw")
                    .Select(f => f.VfsPath));
            }
            catch (Exception ex) { OsConsoleLog.For<MarkdrawApp>().LogSwallowed(ex, "Failed to list workspace drawings from VFS."); }
        }
        return [.. results.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string UnescapeIncomingScene(string content)
    {
        if (content.StartsWith('"') && content.EndsWith('"') && content.Length >= 2)
        {
            try { return System.Text.RegularExpressions.Regex.Unescape(content[1..^1]); }
            catch (Exception ex) { OsConsoleLog.For<MarkdrawApp>().LogSwallowed(ex, "Failed to unescape incoming scene string; returning raw content."); }
        }
        return content;
    }

    private static string NormalizeSceneFormat(string? requestedFormat, string docPath, string content)
    {
        var clean = requestedFormat?.Trim().ToLowerInvariant();
        return clean is "markdraw" or "excalidraw" ? clean : InferFormat(docPath, content);
    }

    private static string InferFormat(string docPath, string content) =>
        docPath.EndsWith(".excalidraw", StringComparison.OrdinalIgnoreCase) || content.TrimStart().StartsWith('{') ? "excalidraw" : "markdraw";

    internal static string NormalizeDocumentPath(string raw)
    {
        var clean = WorkspaceFilesService.ExtractCleanFileName(raw);
        if (string.IsNullOrWhiteSpace(clean)) { return "/work/architecture.markdraw"; }
        if (!Path.HasExtension(clean)) { clean += ".markdraw"; }
        return clean.StartsWith('/') ? clean : $"/work/{clean}";
    }

    private static string NormalizeExportPath(string raw, string defaultFormat)
    {
        var clean = WorkspaceFilesService.ExtractCleanFileName(raw);
        if (string.IsNullOrWhiteSpace(clean)) { return $"/work/architecture.{defaultFormat}"; }
        if (!Path.HasExtension(clean)) { clean += $".{defaultFormat}"; }
        return clean.StartsWith('/') ? clean : $"/work/{clean}";
    }
}
