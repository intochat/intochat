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
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Apps.Markdraw;

[DigitalBrain.Signal("markdraw.open")]
public sealed record MarkdrawOpen(
    string? Document = null,
    string? Template = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("markdraw.read-canvas")]
public sealed record MarkdrawReadCanvas(
    string? Document = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("markdraw.sync-scene")]
public sealed record MarkdrawSyncScene(
    string? Content = null,
    string? Document = null,
    string? Format = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("markdraw.patch-elements")]
public sealed record MarkdrawPatchElements(
    string? Prompt = null,
    string? AddOrUpdateDsl = null,
    string? RemoveIdsCsv = null,
    string? Document = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("markdraw.from-data")]
public sealed record MarkdrawFromData(
    string? Title = null,
    string? ColumnsCsv = null,
    string? RowsSummary = null,
    string? SourceUri = null,
    string? Document = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("markdraw.save")]
public sealed record MarkdrawSave(
    string? Document = null,
    string? Content = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("markdraw.export")]
public sealed record MarkdrawExport(
    string? Format = null,
    string? TargetPath = null) : DigitalBrain.Signal;

public sealed record MarkdrawElementSummary(
    string Id,
    string Kind,
    string Label,
    int X,
    int Y,
    int Width,
    int Height,
    string? FromId = null,
    string? ToId = null,
    string? Color = null);

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

[DigitalBrain.App("markdraw", Accent = "#10B981", Category = "Studio")]
public sealed class MarkdrawApp : DigitalBrain.App<MarkdrawAppState>, IWithFilesVfs
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
        # Nordlys Q4 Data & Deal Pipeline Flow

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

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(MarkdrawApp));

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

    [DigitalBrain.On]
    public async Task Open(MarkdrawOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestedDoc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : NormalizeDocumentPath(request.Document);

        var content = State.Content;
        if (!string.IsNullOrWhiteSpace(request.Template))
        {
            content = SelectTemplateContent(request.Template, requestedDoc);
        }

        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                await EnsureDefaultDrawingsSeededAsync();
                if (await FilesVfs.ReadTextAsync(requestedDoc) is { Length: > 0 } existingText && string.IsNullOrWhiteSpace(request.Template))
                {
                    content = existingText;
                }
                else
                {
                    var written = await FilesVfs.WriteTextAsync(requestedDoc, content);
                    requestedDoc = written.VfsPath;
                }

                drawings = await ListWorkspaceDrawingsAsync(requestedDoc);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs is optional when opening MarkdrawApp in standalone baseline tests.");
            }
        }

        var format = InferFormat(requestedDoc, content);
        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        Set(State with
        {
            ActiveDocument = requestedDoc,
            Format = format,
            Content = content,
            Elements = elements,
            WorkspaceDrawings = drawings,
            Status = $"Opened {requestedDoc} ({elements.Length} canvas element(s), rev {State.Revision})",
        });
    }

    [DigitalBrain.On]
    public async Task ReadCanvas(MarkdrawReadCanvas request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : NormalizeDocumentPath(request.Document);
        var content = State.Content;

        if (FilesVfs is not null)
        {
            try
            {
                if (await FilesVfs.ReadTextAsync(doc) is { Length: > 0 } diskContent)
                {
                    content = diskContent;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs read is optional in standalone MarkdrawApp tests.");
            }
        }

        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        Set(State with
        {
            ActiveDocument = doc,
            Content = content,
            Elements = elements,
            Status = $"Read {elements.Length} element(s) from {doc} (rev {State.Revision})",
        });
    }

    [DigitalBrain.On]
    public async Task SyncScene(MarkdrawSyncScene request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : NormalizeDocumentPath(request.Document);
        var rawContent = string.IsNullOrWhiteSpace(request.Content)
            ? State.Content
            : UnescapeIncomingScene(request.Content);
        var format = string.IsNullOrWhiteSpace(request.Format)
            ? InferFormat(doc, rawContent)
            : request.Format.Trim().ToLowerInvariant();

        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                var saved = await FilesVfs.WriteTextAsync(doc, rawContent);
                doc = saved.VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(doc);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs write is optional when syncing Markdraw scene in unit tests.");
            }
        }

        var elements = MarkdrawSceneEngine.ParseSceneElements(rawContent);
        var nextRev = State.Revision + 1;
        Set(State with
        {
            ActiveDocument = doc,
            Format = format,
            Content = rawContent,
            Revision = nextRev,
            Elements = elements,
            WorkspaceDrawings = drawings,
            LastMutationSummary = $"Synced live canvas to {doc} ({elements.Length} element(s), rev {nextRev})",
            Status = $"Synced {doc} · {elements.Length} element(s) · rev {nextRev}",
        });
    }

    [DigitalBrain.On]
    public async Task PatchElements(MarkdrawPatchElements request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : NormalizeDocumentPath(request.Document);

        var currentContent = State.Content;
        if (FilesVfs is not null)
        {
            try
            {
                if (await FilesVfs.ReadTextAsync(doc) is { Length: > 0 } existing)
                {
                    currentContent = existing;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs read is optional during PatchElements.");
            }
        }

        var (mergedContent, mutationSummary) = MarkdrawSceneEngine.ApplyCoDrawingPatch(
            currentContent,
            request.Prompt,
            request.AddOrUpdateDsl,
            request.RemoveIdsCsv);

        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                var written = await FilesVfs.WriteTextAsync(doc, mergedContent);
                doc = written.VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(doc);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs write is optional during PatchElements.");
            }
        }

        var elements = MarkdrawSceneEngine.ParseSceneElements(mergedContent);
        var nextRev = State.Revision + 1;
        Set(State with
        {
            ActiveDocument = doc,
            Format = InferFormat(doc, mergedContent),
            Content = mergedContent,
            Revision = nextRev,
            Elements = elements,
            WorkspaceDrawings = drawings,
            LastMutationSummary = mutationSummary,
            Status = $"{mutationSummary} · Saved to {doc} (rev {nextRev})",
        });
    }

    [DigitalBrain.On]
    public async Task FromData(MarkdrawFromData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? "Pipeline Opportunities by Amount"
            : request.Title.Trim();
        var doc = string.IsNullOrWhiteSpace(request.Document)
            ? "/work/data-lineage.markdraw"
            : NormalizeDocumentPath(request.Document);
        var cols = (request.ColumnsCsv ?? "Opportunity, Account, Stage, Amount")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rowsSummary = string.IsNullOrWhiteSpace(request.RowsSummary)
            ? "5 rows · Total €627,700 · Top: Enterprise pilot (€240,000)"
            : request.RowsSummary.Trim();
        var sourceUri = string.IsNullOrWhiteSpace(request.SourceUri)
            ? "neuron://local/postgres/main"
            : request.SourceUri.Trim();

        var generated = MarkdrawSceneEngine.BuildDataLineageMarkdraw(title, cols, rowsSummary, sourceUri);
        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                var written = await FilesVfs.WriteTextAsync(doc, generated);
                doc = written.VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(doc);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs write is optional during FromData.");
            }
        }

        var elements = MarkdrawSceneEngine.ParseSceneElements(generated);
        var nextRev = State.Revision + 1;
        var summary = $"Generated data-flow diagram '{title}' ({elements.Length} elements) in {doc}";
        Set(State with
        {
            ActiveDocument = doc,
            Format = "markdraw",
            Content = generated,
            Revision = nextRev,
            Elements = elements,
            WorkspaceDrawings = drawings,
            LastMutationSummary = summary,
            Status = $"{summary} (rev {nextRev})",
        });
    }

    [DigitalBrain.On]
    public async Task Save(MarkdrawSave request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var doc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : NormalizeDocumentPath(request.Document);
        var content = string.IsNullOrWhiteSpace(request.Content)
            ? State.Content
            : UnescapeIncomingScene(request.Content);

        var drawings = State.WorkspaceDrawings;
        if (FilesVfs is not null)
        {
            try
            {
                var written = await FilesVfs.WriteTextAsync(doc, content);
                doc = written.VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(doc);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs write is optional during Save.");
            }
        }

        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        Set(State with
        {
            ActiveDocument = doc,
            Format = InferFormat(doc, content),
            Content = content,
            Elements = elements,
            WorkspaceDrawings = drawings,
            Status = $"Saved {doc} ({elements.Length} element(s), rev {State.Revision})",
        });
    }

    [DigitalBrain.On]
    public async Task Export(MarkdrawExport request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = string.IsNullOrWhiteSpace(request.Format)
            ? "excalidraw"
            : request.Format.Trim().TrimStart('.').ToLowerInvariant();
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
                var saved = await FilesVfs.WriteTextAsync(targetPath, exportedPayload);
                targetPath = saved.VfsPath;
                drawings = await ListWorkspaceDrawingsAsync(State.ActiveDocument);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "FilesVfs write is optional during Export.");
            }
        }

        Set(State with
        {
            LastExportPath = targetPath,
            WorkspaceDrawings = drawings,
            Status = $"Exported {State.Elements.Length} element(s) to {targetPath} ({format})",
        });
    }

    public View RenderSide()
    {
        var drawings = State.WorkspaceDrawings is { Length: > 0 }
            ? State.WorkspaceDrawings
            : [State.ActiveDocument, "/work/pipeline-flow.markdraw"];

        return new(
            "Markdraw Files",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill("Markdraw Studio", "accent", "pen-tool"),
                            new TagPill($"rev {State.Revision}", "ok"),
                        ]) { Gap = 6 },
                        new Text(State.ActiveDocument) { Mono = true, Tabular = true },
                        new Text($"{State.Elements.Length} element(s) on canvas") { Tone = "muted", Tabular = true },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Live Co-Drawing Canvas"),
                new Divider("WORKSPACE DRAWINGS (/work)"),
                .. drawings.Select(path => (Node)UI.NavItem(
                    $"drawing-{MarkdrawSceneEngine.SlugId(path)}",
                    Path.GetFileName(path),
                    path.EndsWith(".excalidraw", StringComparison.OrdinalIgnoreCase) ? "file-json" : "pen-tool",
                    $"open:{path}",
                    selected: string.Equals(path, State.ActiveDocument, StringComparison.OrdinalIgnoreCase),
                    badge: string.Equals(path, State.ActiveDocument, StringComparison.OrdinalIgnoreCase) ? "LIVE" : null)),
                new Divider("TEMPLATES"),
                UI.NavItem("tpl-arch", "System Architecture", "cpu", "open:/work/architecture.markdraw"),
                UI.NavItem("tpl-pipeline", "CRM Data Pipeline", "git-branch", "open:/work/pipeline-flow.markdraw"),
            ])
            { Gap = 8 });
    }

    public View Render()
    {
        var shapeCount = State.Elements.Count(e => e.Kind != "arrow" && e.Kind != "line");
        var connectorCount = State.Elements.Count(e => e.Kind is "arrow" or "line");

        return new(
            "Markdraw",
            new Column(
            [
                new Surface(
                    [
                        new Row(
                        [
                            new TagPill("Markdraw · Excalidraw Canvas", "accent", "pen-tool"),
                            new TagPill(State.ActiveDocument, "info", "folder"),
                            new TagPill($"{shapeCount} shape(s) · {connectorCount} arrow(s)", "ok"),
                            new TagPill($"rev {State.Revision}", "neutral"),
                        ]) { Gap = 8 },
                        new Text(State.Status) { Tabular = true },
                        new Row(
                        [
                            new Button("save-to-files", "Save to Files", Emit: AppSignals.Emit("markdraw", "save", State.ActiveDocument), Tone: ButtonTone.Accent)
                            {
                                Icon = "save",
                                Small = true,
                            },
                            new Button("export-excalidraw", "Export .excalidraw", Emit: AppSignals.Emit("markdraw", "export", "excalidraw"))
                            {
                                Icon = "file-json",
                                Small = true,
                            },
                            new Button("export-svg", "Export .svg", Emit: AppSignals.Emit("markdraw", "export", "svg"))
                            {
                                Icon = "image",
                                Small = true,
                            },
                            new Button("diagram-crm-data", "Diagram CRM Data", Emit: AppSignals.Emit("markdraw", "from-data", "Pipeline Opportunities by Amount"))
                            {
                                Icon = "bar-chart-2",
                                Small = true,
                            },
                        ]) { Gap = 8 },
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: $"Markdraw Studio · {Path.GetFileName(State.ActiveDocument)}"),
                MarkdrawSceneEngine.BuildMarkdrawCanvasCard(
                    key: $"markdraw-canvas-{State.Revision}",
                    title: $"Markdraw Canvas · {Path.GetFileName(State.ActiveDocument)}",
                    activeDocument: State.ActiveDocument,
                    format: State.Format,
                    content: State.Content,
                    revision: State.Revision,
                    status: State.Status,
                    elements: State.Elements,
                    workspaceDrawings: State.WorkspaceDrawings,
                    lastExportPath: State.LastExportPath),
                new DataTable(
                    $"Canvas Elements Inventory ({State.Elements.Length} items in {State.ActiveDocument})",
                    ["ID", "Kind", "Label / Connection", "Geometry"],
                    State.Elements
                        .Select(e => (IReadOnlyList<string>)
                        [
                            e.Id,
                            e.Kind,
                            e.Kind is "arrow" or "line" && !string.IsNullOrWhiteSpace(e.FromId)
                                ? $"{e.FromId} -> {e.ToId}" + (string.IsNullOrWhiteSpace(e.Label) ? string.Empty : $" ({e.Label})")
                                : e.Label,
                            e.Kind is "arrow" or "line" ? "connector" : $"{e.X},{e.Y} ({e.Width}x{e.Height})",
                        ])
                        .ToArray(),
                    SourceNeuronUri: "neuron://local/apps/markdraw#elements",
                    From: "Markdraw"),
            ])
            { Gap = 12 });
    }


    [DigitalBrain.Scenario("Markdraw opens workspace canvas, co-draws new architecture nodes with Ino, and exports Excalidraw JSON")]
    public async Task VerifyMarkdrawCoDrawingAndPersistenceScenario()
    {
        await Open(new MarkdrawOpen("/work/architecture.markdraw"));
        if (State.Elements.Length < 5)
        {
            throw new InvalidOperationException("Expected MarkdrawApp.Open to parse initial architecture canvas elements.");
        }

        await PatchElements(new MarkdrawPatchElements(
            Prompt: "Add Redis Cache node connected to Data Analytics Engine",
            AddOrUpdateDsl: "rect \"Redis Cache\" id=redis-cache at 650,410 size 220x76 fill=#7f1d1d color=#f87171 rounded=10\narrow data-engine -> redis-cache \"Sub-ms Cache\" color=#f87171",
            Document: "/work/architecture.markdraw"));

        if (!State.Elements.Any(e => string.Equals(e.Id, "redis-cache", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Expected MarkdrawApp.PatchElements to merge redis-cache into the active canvas.");
        }

        await Export(new MarkdrawExport("excalidraw", "/work/architecture.excalidraw"));
        if (!string.Equals(State.LastExportPath, "/work/architecture.excalidraw", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Expected MarkdrawApp.Export to write /work/architecture.excalidraw.");
        }
    }

    private async Task EnsureDefaultDrawingsSeededAsync()
    {
        if (FilesVfs is null)
        {
            return;
        }

        if (await FilesVfs.ResolveFileAsync("/work/architecture.markdraw") is null)
        {
            await FilesVfs.WriteTextAsync("/work/architecture.markdraw", DefaultArchitectureScene);
        }

        if (await FilesVfs.ResolveFileAsync("/work/pipeline-flow.markdraw") is null)
        {
            await FilesVfs.WriteTextAsync("/work/pipeline-flow.markdraw", DefaultPipelineScene);
        }
    }

    private async Task<string[]> ListWorkspaceDrawingsAsync(string activeDoc)
    {
        if (FilesVfs is null)
        {
            return [activeDoc, "/work/architecture.markdraw", "/work/pipeline-flow.markdraw"];
        }

        var files = await FilesVfs.ListFilesAsync();
        var paths = files
            .Where(f => IsMarkdrawExtension(f.Name))
            .Select(f => f.VfsPath)
            .Append(activeDoc)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return paths.Length > 0 ? paths : [activeDoc];
    }

    public static bool IsMarkdrawExtension(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return false;
        }

        var ext = Path.GetExtension(fileNameOrPath).ToLowerInvariant();
        return ext is ".markdraw" or ".excalidraw";
    }

    public static string NormalizeDocumentPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "/work/architecture.markdraw";
        }

        var clean = WorkspaceFilesService.ExtractCleanFileName(input);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return "/work/architecture.markdraw";
        }

        if (!Path.HasExtension(clean))
        {
            clean += ".markdraw";
        }

        return clean.StartsWith('/') ? clean : $"/work/{clean}";
    }

    private static string NormalizeExportPath(string targetPath, string format)
    {
        var clean = WorkspaceFilesService.ExtractCleanFileName(targetPath);
        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = $"architecture.{format}";
        }
        else if (!Path.HasExtension(clean))
        {
            clean = $"{clean}.{format}";
        }

        return clean.StartsWith('/') ? clean : $"/work/{clean}";
    }

    private static string SelectTemplateContent(string template, string documentPath)
    {
        var normalized = template.Trim().ToLowerInvariant();
        if (normalized.Contains("pipeline", StringComparison.Ordinal)
            || normalized.Contains("crm", StringComparison.Ordinal)
            || normalized.Contains("data", StringComparison.Ordinal)
            || documentPath.Contains("pipeline", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultPipelineScene;
        }

        return DefaultArchitectureScene;
    }

    private static string InferFormat(string documentPath, string content)
    {
        if (documentPath.EndsWith(".excalidraw", StringComparison.OrdinalIgnoreCase)
            || (content.TrimStart().StartsWith('{') && content.Contains("\"excalidraw\"", StringComparison.OrdinalIgnoreCase)))
        {
            return "excalidraw";
        }

        return "markdraw";
    }

    private static string UnescapeIncomingScene(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                return Uri.UnescapeDataString(trimmed);
            }
            catch (UriFormatException)
            {
                return trimmed;
            }
        }

        return trimmed;
    }
}
