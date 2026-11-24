using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Markdraw;

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

/// <summary>
/// Immutable DDD Aggregate state for <see cref="MarkdrawApp"/> (<c>app.markdraw</c>).
/// </summary>
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
    public const int MinZoomPercent = 25;
    public const int MaxZoomPercent = 400;
    public const int MinLayer = 1;
    public const int MaxLayer = 8;
    public const int MaxCanvasElements = 128;

    public static readonly string[] SupportedTools =
    [
        "select", "hand", "rect", "diamond", "ellipse", "arrow", "freedraw", "text", "frame", "note",
    ];

    public static readonly string[] SupportedShapeKinds =
    [
        "rect", "diamond", "ellipse", "frame", "text", "note",
    ];

    public static readonly string[] SupportedTemplates =
    [
        "architecture", "pipeline", "crm", "sales",
    ];

    public static readonly string[] SupportedExportFormats =
    [
        "excalidraw", "svg", "markdraw",
    ];

    public MarkdrawAppState() : this(
        "/work/architecture.markdraw",
        "markdraw",
        MarkdrawApp.DefaultArchitectureScene,
        1,
        "Markdraw Canvas Ready · Co-drawing enabled with Ino",
        MarkdrawSceneEngine.ParseSceneElements(MarkdrawApp.DefaultArchitectureScene),
        ["/work/architecture.markdraw", "/work/pipeline-flow.markdraw"])
    {
    }

    public string ActiveTool { get; init; } = "select";

    public int ZoomPercent { get; init; } = 100;

    public int ActiveLayer { get; init; } = 1;

    public string? SelectedElementId { get; init; }

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("Canvas revision is strictly positive."), JsonIgnore]
    public bool PositiveRevision => Revision >= 1;

    [Invariant("Scene format is always markdraw or excalidraw."), JsonIgnore]
    public bool ValidSceneFormat => Format is "markdraw" or "excalidraw";

    [Invariant("Active document, diagram source, and non-empty canvas elements are valid and bounded."), JsonIgnore]
    public bool NonNullScene =>
        !string.IsNullOrWhiteSpace(ActiveDocument)
        && !string.IsNullOrWhiteSpace(Content)
        && Elements is { Length: > 0 and <= MaxCanvasElements }
        && Elements.All(static e => !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.Kind))
        && WorkspaceDrawings is not null;

    [Invariant("ActiveTool is always a supported Markdraw studio tool."), JsonIgnore]
    public bool ValidActiveTool => SupportedTools.Contains(ActiveTool, StringComparer.Ordinal);

    [Invariant("Canvas zoom stays within 25%–400% bounds."), JsonIgnore]
    public bool ValidZoomBounds => ZoomPercent is >= MinZoomPercent and <= MaxZoomPercent;

    [Invariant("Active canvas layer stays within 1–8 bounds."), JsonIgnore]
    public bool ValidLayerBounds => ActiveLayer is >= MinLayer and <= MaxLayer;

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public static Rule SupportedToolRule(string? tool) => Decision.Rule(
        "invalid_tool",
        tool,
        static t => string.IsNullOrWhiteSpace(t) || !SupportedTools.Contains(t.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Canvas tool must be select, hand, rect, diamond, ellipse, arrow, freedraw, text, frame, or note.");

    public static Rule SupportedShapeKindRule(string? kind) => Decision.Rule(
        "invalid_shape_kind",
        kind,
        static k => string.IsNullOrWhiteSpace(k) || !SupportedShapeKinds.Contains(k.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Shape kind must be rect, diamond, ellipse, frame, text, or note.");

    public static Rule NonEmptyShapeLabelRule(string? label) => Decision.Rule(
        "empty_shape_label",
        label,
        static l => string.IsNullOrWhiteSpace(l),
        "Enter a non-empty label for the canvas shape.");

    public static Rule ValidZoomRule(int zoomPercent) => Decision.Rule(
        "zoom_out_of_bounds",
        zoomPercent,
        static z => z < MinZoomPercent || z > MaxZoomPercent,
        static z => string.Create(CultureInfo.InvariantCulture, $"Canvas zoom must stay between {MinZoomPercent}% and {MaxZoomPercent}% (received {z}%)."));

    public static Rule ValidLayerRule(int layer) => Decision.Rule(
        "layer_out_of_bounds",
        layer,
        static l => l < MinLayer || l > MaxLayer,
        static l => string.Create(CultureInfo.InvariantCulture, $"Canvas layer must stay between {MinLayer} and {MaxLayer} (received {l})."));

    public static Rule SupportedTemplateRule(string? template) => Decision.Rule(
        "unsupported_template",
        template,
        static t => string.IsNullOrWhiteSpace(t) || !SupportedTemplates.Contains(t.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Template must be architecture or pipeline.");

    public Rule HasCanvasElements() => Decision.Rule(
        "canvas_empty",
        Elements,
        static e => e is null || e.Length == 0,
        "Add at least one element before exporting.");

    public Rule WithinCanvasCapacityRule() => Decision.Rule(
        "canvas_capacity_reached",
        Elements?.Length ?? 0,
        static count => count >= MaxCanvasElements,
        string.Create(CultureInfo.InvariantCulture, $"Canvas element limit ({MaxCanvasElements}) reached."));

    public static Rule NonEmptyDiagramSceneRule(string? content, MarkdrawElementSummary[] parsedElements) => Decision.Rule(
        "empty_diagram_scene",
        (Content: content, Count: parsedElements.Length),
        static x => string.IsNullOrWhiteSpace(x.Content) || x.Count == 0,
        "Diagram scene must contain at least one canvas element.");

    public Rule SupportedExportFormat(string? format) => Decision.Rule(
        "invalid_export_format",
        format,
        static f => string.IsNullOrWhiteSpace(f) || !SupportedExportFormats.Contains(f.Trim().TrimStart('.').ToLowerInvariant(), StringComparer.Ordinal),
        "Export format must be excalidraw, svg, or markdraw.");

    public static Rule NonEmptyExportPathRule(string? targetPath) => Decision.Rule(
        "empty_export_path",
        targetPath,
        static p => string.IsNullOrWhiteSpace(p),
        "Target export path is required.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public Decision<MarkdrawAppState> SelectTool([Sample("select", "rect", "arrow", "laser-invalid")] string? tool)
    {
        if (Decision.Check("select-tool", SupportedToolRule(tool)) is { } refusal)
        {
            return refusal;
        }

        var cleanTool = tool!.Trim().ToLowerInvariant();
        return Decision.Accept(this with
        {
            ActiveTool = cleanTool,
            Status = $"Selected '{cleanTool}' tool · {ZoomPercent}% · Layer {ActiveLayer}",
        });
    }

    public Decision<MarkdrawAppState> SetZoom([Sample(100, 150, 10, 500)] int zoomPercent)
    {
        if (Decision.Check("set-zoom", ValidZoomRule(zoomPercent)) is { } refusal)
        {
            return refusal;
        }

        return Decision.Accept(this with
        {
            ZoomPercent = zoomPercent,
            Status = string.Create(CultureInfo.InvariantCulture, $"Zoom set to {zoomPercent}% · {Elements.Length} element(s)"),
        });
    }

    public Decision<MarkdrawAppState> SelectLayer([Sample(1, 2, 0, 99)] int layer)
    {
        if (Decision.Check("select-layer", ValidLayerRule(layer)) is { } refusal)
        {
            return refusal;
        }

        return Decision.Accept(this with
        {
            ActiveLayer = layer,
            Status = string.Create(CultureInfo.InvariantCulture, $"Switched to Layer {layer} · {Elements.Length} element(s)"),
        });
    }

    public Decision<MarkdrawAppState> AddShape(
        [Sample("rect", "hexagon-invalid")] string? kind,
        [Sample("API Gateway", "")] string? label)
    {
        if (Decision.Check(
                "add-shape",
                SupportedShapeKindRule(kind),
                NonEmptyShapeLabelRule(label),
                WithinCanvasCapacityRule()) is { } refusal)
        {
            return refusal;
        }

        var cleanKind = kind!.Trim().ToLowerInvariant();
        var cleanLabel = label!.Trim().Replace("\"", "'", StringComparison.Ordinal);
        var slug = MarkdrawSceneEngine.SlugId(cleanLabel);
        var id = $"{cleanKind}-{slug}";
        var x = 100 + Elements.Length % 3 * 250;
        var y = 120 + Elements.Length / 3 * 120;
        var dsl = string.Create(
            CultureInfo.InvariantCulture,
            $"{cleanKind} \"{cleanLabel}\" id={id} at {x},{y} size 220x80 fill=#064e3b color=#10b981 rounded=10");

        var (mergedContent, summary) = MarkdrawSceneEngine.ApplyCoDrawingPatch(Content, prompt: null, explicitDsl: dsl, removeIdsCsv: null);
        var elements = MarkdrawSceneEngine.ParseSceneElements(mergedContent);
        var nextRev = Revision + 1;

        return Decision.Accept(this with
        {
            Content = mergedContent,
            Elements = elements,
            Revision = nextRev,
            ActiveTool = cleanKind,
            SelectedElementId = id,
            LastMutationSummary = summary,
            Status = string.Create(CultureInfo.InvariantCulture, $"Added {cleanKind} '{cleanLabel}' ({elements.Length} element(s), rev {nextRev})"),
        });
    }

    public Decision<MarkdrawAppState> SwitchTemplate([Sample("architecture", "pipeline", "unknown")] string template)
    {
        if (Decision.Check("switch-template", SupportedTemplateRule(template)) is { } refusal)
        {
            return refusal;
        }

        var clean = template.Trim().ToLowerInvariant();
        var isPipeline = clean is "pipeline" or "crm" or "sales";
        var doc = isPipeline ? "/work/pipeline-flow.markdraw" : "/work/architecture.markdraw";
        var content = isPipeline ? MarkdrawApp.DefaultPipelineScene : MarkdrawApp.DefaultArchitectureScene;
        var elements = MarkdrawSceneEngine.ParseSceneElements(content);
        var nextRev = Revision + 1;
        return Decision.Accept(this with
        {
            ActiveDocument = doc,
            Format = "markdraw",
            Content = content,
            Revision = nextRev,
            Elements = elements,
            SelectedElementId = null,
            Status = string.Create(CultureInfo.InvariantCulture, $"Opened {doc} ({elements.Length} canvas element(s), rev {nextRev})"),
        });
    }

    public Decision<MarkdrawAppState> RecordExport(
        [Sample("excalidraw", "bmp")] string format,
        [Sample("/work/architecture.excalidraw")] string targetPath)
    {
        if (Decision.Check("export", HasCanvasElements(), SupportedExportFormat(format), NonEmptyExportPathRule(targetPath)) is { } refusal)
        {
            return refusal;
        }

        var cleanFormat = format.Trim().TrimStart('.').ToLowerInvariant();
        var trimmedPath = targetPath.Trim();
        return Decision.Accept(this with
        {
            LastExportPath = trimmedPath,
            Status = string.Create(CultureInfo.InvariantCulture, $"Exported {Elements.Length} element(s) to {trimmedPath} ({cleanFormat})"),
        });
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(ActiveDocument)} · {ActiveTool} · {ZoomPercent}% · L{ActiveLayer} · rev {Revision}");
}
