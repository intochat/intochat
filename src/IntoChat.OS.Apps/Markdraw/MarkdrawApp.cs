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
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using UI = DigitalBrain.UI.Ui;

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
        "/work/architecture.markdraw",
        "markdraw",
        MarkdrawApp.DefaultArchitectureScene,
        1,
        "Markdraw Canvas Ready · Co-drawing enabled with Ino & Files VFS",
        MarkdrawSceneEngine.ParseSceneElements(MarkdrawApp.DefaultArchitectureScene),
        ["/work/architecture.markdraw", "/work/pipeline-flow.markdraw"])
    {
    }
}

[Neuron("app.markdraw")]
public sealed class MarkdrawApp : Screen<MarkdrawAppState>, IWithFilesVfs
{
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
        "markdraw",
        "1.0.0",
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
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Dock: 4, Studio: true,
            Toolbar:
            [
                new("open", Icon: "pen-tool", Label: "Open Canvas"),
                new("save", Icon: "save", Label: "Save to Files"),
                new("export", Icon: "download", Label: "Export .excalidraw"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(MarkdrawApp).AssemblyQualifiedName,
    };

    [On]
    public async Task Open(MarkdrawOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var content = !string.IsNullOrWhiteSpace(request.Template)
            ? SelectTemplateContent(request.Template, doc)
            : State.Content;

        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                await EnsureDefaultDrawingsSeededAsync();
                if (string.IsNullOrWhiteSpace(request.Template) && await FilesVfs.ReadTextAsync(doc) is { Length: > 0 } existingText)
                {
                    content = existingText;
                }
                else
                {
                    doc = (await FilesVfs.WriteTextAsync(doc, content)).VfsPath;
                }
                drawings = await ListWorkspaceDrawingsAsync(doc);
            }
            catch (Exception ex)
            {
                _ = ex.Message;
            }
        }

        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        await Show(State with
        {
            ActiveDocument = doc,
            Format = InferFormat(doc, content),
            Content = content,
            Elements = elements,
            WorkspaceDrawings = drawings,
            Status = $"Opened {doc} ({elements.Length} canvas element(s), rev {State.Revision})",
        });
    }

    [On]
    public async Task ReadCanvas(MarkdrawReadCanvas request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var content = await ReadVfsOrCurrentContentAsync(doc, "FilesVfs read is optional in standalone MarkdrawApp tests.");
        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        await Show(State with
        {
            ActiveDocument = doc,
            Content = content,
            Elements = elements,
            Status = $"Read {elements.Length} element(s) from {doc} (rev {State.Revision})",
        });
    }

    [On]
    public async Task SyncScene(MarkdrawSyncScene request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var rawContent = string.IsNullOrWhiteSpace(request.Content) ? State.Content : UnescapeIncomingScene(request.Content);
        var format = string.IsNullOrWhiteSpace(request.Format) ? InferFormat(doc, rawContent) : request.Format.Trim().ToLowerInvariant();
        var (savedDoc, drawings) = await PersistSceneAsync(doc, rawContent, "FilesVfs write is optional when syncing Markdraw scene in unit tests.");
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
        var currentContent = await ReadVfsOrCurrentContentAsync(doc, "FilesVfs read is optional during PatchElements.");
        var (mergedContent, mutationSummary) = MarkdrawSceneEngine.ApplyCoDrawingPatch(currentContent, request.Prompt, request.AddOrUpdateDsl, request.RemoveIdsCsv);
        var (savedDoc, drawings) = await PersistSceneAsync(doc, mergedContent, "FilesVfs write is optional during PatchElements.");
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
        var (savedDoc, drawings) = await PersistSceneAsync(doc, generated, "FilesVfs write is optional during FromData.");
        var elements = MarkdrawSceneEngine.ParseSceneElements(generated);
        var nextRev = State.Revision + 1;
        var summary = $"Generated data-flow diagram '{title}' ({elements.Length} elements) in {savedDoc}";
        await Show(State with
        {
            ActiveDocument = savedDoc,
            Format = "markdraw",
            Content = generated,
            Revision = nextRev,
            Elements = elements,
            WorkspaceDrawings = drawings,
            LastMutationSummary = summary,
            Status = $"{summary} (rev {nextRev})",
        });
    }

    [On]
    public async Task Save(MarkdrawSave request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = ResolveTargetDoc(request.Document);
        var content = string.IsNullOrWhiteSpace(request.Content) ? State.Content : UnescapeIncomingScene(request.Content);
        var (savedDoc, drawings) = await PersistSceneAsync(doc, content, "FilesVfs write is optional during Save.");
        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        await Show(State with
        {
            ActiveDocument = savedDoc,
            Format = InferFormat(savedDoc, content),
            Content = content,
            Elements = elements,
            WorkspaceDrawings = drawings,
            Status = $"Saved {savedDoc} ({elements.Length} element(s), rev {State.Revision})",
        });
    }

    [On]
    public async Task Export(MarkdrawExport request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = string.IsNullOrWhiteSpace(request.Format) ? "excalidraw" : request.Format.Trim().TrimStart('.').ToLowerInvariant();
        if (format is not ("excalidraw" or "svg" or "markdraw"))
        {
            format = "excalidraw";
        }

        var baseStem = Path.GetFileNameWithoutExtension(State.ActiveDocument);
        if (string.IsNullOrWhiteSpace(baseStem))
        {
            baseStem = "architecture";
        }

        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath)
            ? $"/work/{baseStem}.{format}"
            : NormalizeExportPath(request.TargetPath, format);

        var exportedPayload = format switch
        {
            "svg" => MarkdrawSceneEngine.RenderSvgPreview(State.Elements),
            "markdraw" => State.Content,
            _ => MarkdrawSceneEngine.RenderExcalidrawJson(State.Elements),
        };

        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                targetPath = (await FilesVfs.WriteTextAsync(targetPath, exportedPayload)).VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(State.ActiveDocument);
            }
            catch (Exception ex)
            {
                _ = ex.Message;
            }
        }

        await Show(State with
        {
            LastExportPath = targetPath,
            WorkspaceDrawings = drawings,
            Status = $"Exported {State.Elements.Length} element(s) to {targetPath} ({format})",
        });
    }

    public new MarkdrawAppState State => base.State;
    public View Render() => Render(State);

    private static new Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
        return new Emit("app.markdraw", alias, JsonSerializer.SerializeToElement(signal));
    }
    protected override View Render(MarkdrawAppState state)
    {
        var shapeCount = state.Elements.Count(e => e.Kind is not ("arrow" or "line"));
        var connectorCount = state.Elements.Count(e => e.Kind is "arrow" or "line");

        return new(
            "Markdraw",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill("Markdraw · Excalidraw Canvas", "accent", "pen-tool"),
                            new TagPill(state.ActiveDocument, "info", "folder"),
                            new TagPill($"{shapeCount} shape(s) · {connectorCount} arrow(s)", "ok"),
                            new TagPill($"rev {state.Revision}", "neutral"),
                        ]) { Gap = 8 },
                        new Text(state.Status) { Tabular = true },
                        new Row(
                        [
                            new Button("save-to-files", "Save to Files", Emit: Emits(new MarkdrawSave(state.ActiveDocument)), Tone: ButtonTone.Accent) { Icon = "save", Small = true },
                            new Button("export-excalidraw", "Export .excalidraw", Emit: Emits(new MarkdrawExport("excalidraw"))) { Icon = "file-json", Small = true },
                            new Button("export-svg", "Export .svg", Emit: Emits(new MarkdrawExport("svg"))) { Icon = "image", Small = true },
                            new Button("diagram-crm-data", "Diagram CRM Data", Emit: Emits(new MarkdrawFromData("Pipeline Opportunities by Amount"))) { Icon = "bar-chart-2", Small = true },
                        ]) { Gap = 8 },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: $"Markdraw Studio · {Path.GetFileName(state.ActiveDocument)}"),
                MarkdrawSceneEngine.BuildMarkdrawCanvasCard(
                    key: $"markdraw-canvas-{state.Revision}",
                    title: $"Markdraw Canvas · {Path.GetFileName(state.ActiveDocument)}",
                    activeDocument: state.ActiveDocument,
                    format: state.Format,
                    content: state.Content,
                    revision: state.Revision,
                    status: state.Status,
                    elements: state.Elements,
                    workspaceDrawings: state.WorkspaceDrawings,
                    lastExportPath: state.LastExportPath),
                new DataTable(
                    $"Canvas Elements Inventory ({state.Elements.Length} items in {state.ActiveDocument})",
                    ["ID", "Kind", "Label / Connection", "Geometry"],
                    state.Elements.Select(e => (IReadOnlyList<string>)
                    [
                        e.Id,
                        e.Kind,
                        e.Kind is "arrow" or "line" && !string.IsNullOrWhiteSpace(e.FromId)
                            ? $"{e.FromId} -> {e.ToId}" + (string.IsNullOrWhiteSpace(e.Label) ? string.Empty : $" ({e.Label})")
                            : e.Label,
                        e.Kind is "arrow" or "line" ? "connector" : $"{e.X},{e.Y} ({e.Width}x{e.Height})",
                    ]).ToArray(),
                    SourceNeuronUri: "neuron://local/apps/markdraw#elements",
                    From: "Markdraw"),
            ])
            { Gap = 12 });
    }

    private string ResolveTargetDoc(string? requestedDoc)
        => string.IsNullOrWhiteSpace(requestedDoc)
            ? (string.IsNullOrWhiteSpace(State.ActiveDocument) ? "/work/architecture.markdraw" : State.ActiveDocument)
            : NormalizeDocumentPath(requestedDoc);

    private static string SelectTemplateContent(string template, string targetDoc)
    {
        var clean = template.Trim().ToLowerInvariant();
        if (clean is "pipeline" or "crm" or "sales" || targetDoc.Contains("pipeline", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultPipelineScene;
        }
        return DefaultArchitectureScene;
    }

    private async Task EnsureDefaultDrawingsSeededAsync()
    {
        if (FilesVfs is null) { return; }
        try
        {
            var arch = await FilesVfs.ResolveFileAsync("/work/architecture.markdraw");
            if (arch is null)
            {
                await FilesVfs.WriteTextAsync("/work/architecture.markdraw", DefaultArchitectureScene);
            }

            var pipeline = await FilesVfs.ResolveFileAsync("/work/pipeline-flow.markdraw");
            if (pipeline is null)
            {
                await FilesVfs.WriteTextAsync("/work/pipeline-flow.markdraw", DefaultPipelineScene);
            }
        }
        catch (Exception ex)
        {
            _ = ex.Message;
        }
    }

    private async Task<string> ReadVfsOrCurrentContentAsync(string docPath, string reason)
    {
        _ = reason;
        if (FilesVfs is not null)
        {
            try
            {
                if (await FilesVfs.ReadTextAsync(docPath) is { Length: > 0 } text)
                {
                    return text;
                }
            }
            catch (Exception ex)
            {
                _ = ex.Message;
            }
        }
        return State.Content;
    }

    private async Task<(string SavedPath, string[] Drawings)> PersistSceneAsync(string docPath, string content, string reason)
    {
        _ = reason;
        var saved = docPath;
        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                saved = (await FilesVfs.WriteTextAsync(docPath, content)).VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(saved);
            }
            catch (Exception ex)
            {
                _ = ex.Message;
            }
        }
        return (saved, drawings);
    }

    private async Task<string[]> ListWorkspaceDrawingsAsync(string activePath)
    {
        var results = new List<string>();
        if (!string.IsNullOrWhiteSpace(activePath))
        {
            results.Add(activePath);
        }

        if (FilesVfs is not null)
        {
            try
            {
                foreach (var file in await FilesVfs.ListFilesAsync())
                {
                    var ext = Path.GetExtension(file.Name).ToLowerInvariant();
                    if (ext is ".markdraw" or ".excalidraw")
                    {
                        results.Add(file.VfsPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _ = ex.Message;
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string UnescapeIncomingScene(string content)
    {
        if (content.StartsWith('"') && content.EndsWith('"') && content.Length >= 2)
        {
            try { return System.Text.RegularExpressions.Regex.Unescape(content[1..^1]); }
            catch (Exception ex)
            {
                _ = ex;
                return content;
            }
        }
        return content;
    }

    private static string InferFormat(string docPath, string content)
    {
        if (docPath.EndsWith(".excalidraw", StringComparison.OrdinalIgnoreCase)
            || content.TrimStart().StartsWith('{'))
        {
            return "excalidraw";
        }
        return "markdraw";
    }

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
