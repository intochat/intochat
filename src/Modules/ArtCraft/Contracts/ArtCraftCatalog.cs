using System.Collections.Frozen;

namespace IntoChat.OS.Modules.ArtCraft;

[Flags]
public enum ArtCraftCapabilities
{
    None = 0,
    WasmUi = 1 << 0,
    McpServer = 1 << 1,
    NativeCli = 1 << 2,
    All = WasmUi | McpServer | NativeCli,
}

public sealed record ArtCraftAppDescriptor(
    string Id,
    string Title,
    string Subtitle,
    string Description,
    string Category,
    string AccentHex,
    string Icon,
    int DefaultWebPort,
    int DefaultMcpPort,
    string CliBinary,
    string DefaultDocument,
    string DefaultOutputDocument,
    string DefaultCommandId,
    string DefaultCommandTitle,
    string DefaultParametersJson,
    string[] SupportedExtensions,
    string[] DefaultLayersOrTracks,
    ArtCraftCapabilities Capabilities = ArtCraftCapabilities.All,
    string? WebReleaseVersion = null)
{
    public string IconKey => $"artcraft:{Id}";
    public string DefaultWebUrl => $"http://localhost:{DefaultWebPort}/";
    public string DefaultMcpUrl => $"http://localhost:{DefaultMcpPort}/mcp";
    public string StaticWebPath => $"/artcraft/{Id}/index.html";
    public string? WebReleaseZipUrl => string.IsNullOrWhiteSpace(WebReleaseVersion)
        ? null
        : $"https://github.com/storytold/{Id}/releases/download/v{WebReleaseVersion}/{Id}-web-{WebReleaseVersion}.zip";
}

/// <summary>
/// Single source of truth for the getartcraft.com (storytold) open-source Rust creative and productivity suite.
/// Eliminates hardcoded app names, ports, file extensions, and capabilities across the OS kernel, Aspire hosting, and UI.
/// </summary>
public static class ArtCraftCatalog
{
    public static readonly IReadOnlyList<ArtCraftAppDescriptor> All =
    [
        new("artcraft", "ArtCraft", "Unified Creative Suite & Launcher",
            "Master creative suite hub and launcher for the open-source Rust ArtCraft ecosystem (WASM Web UI, MCP Server, and craft-cli).",
            "Studio", "#6366F1", "artcraft:artcraft", 18080, 18098, "craft-cli", "/work/project.craft", "/work/project.export.png",
            "suite.inspect", "Inspect ArtCraft Workspace", "{\"suite\":\"artcraft\"}", [".craft"],
            ["ArtCraft Suite Hub · Connected", "Shared /work Volume · Mounted"]),
        new("photocraft", "PhotoCraft", "Layered Raster & Photo Studio",
            "Photoshop-compatible layered image studio for editing images, removing backgrounds, adjusting levels, and generating graphics.",
            "Studio", "#06B6D4", "artcraft:photocraft", 18082, 18100, "photocraft-cli", "/work/portrait.png", "/work/portrait.nobg.png",
            "layer.removeBackground", "Remove Background (AI Subject Alpha Mask)", "{\"featherPx\":1,\"output\":\"layer-mask\"}",
            [".png", ".jpg", ".jpeg", ".webp", ".psd", ".avif", ".gif", ".tiff"],
            ["Subject Layer (100% · Normal)", "Background Layer (Locked · 100%)"], WebReleaseVersion: "0.21.0"),
        new("filmcraft", "FilmCraft", "Non-Linear Video Editor",
            "Premiere-compatible non-linear video editor (VideoCraft / FilmCraft) with full-card WASM Web UI, Native Linux CLI (filmcraft-cli), and MCP Server.",
            "Studio", "#8B5CF6", "artcraft:filmcraft", 18083, 18101, "filmcraft-cli", "/work/launch-trailer.fcproj", "/work/launch-trailer.cut.mp4",
            "timeline.razor", "Razor Split Clip at Playhead (00:00:03:12)", "{\"timecode\":\"00:00:03:12\",\"track\":\"V1\"}",
            [".mp4", ".mov", ".fcproj", ".mkv", ".webm"],
            ["V2 · Title Overlay (00:00:00:00 -> 00:00:05:00)", "V1 · A-Roll Main Camera (00:00:00:00 -> 00:00:14:00)", "A1 · Master Dialogue Bus (-14 LUFS)"], WebReleaseVersion: "0.18.0"),
        new("vectorcraft", "VectorCraft", "Vector Illustration & Bézier Studio",
            "Illustrator-compatible vector design studio with Bézier anchors, boolean pathfinder operations, live blends, and SVG/PDF export.",
            "Studio", "#F59E0B", "artcraft:vectorcraft", 18084, 18102, "vectorcraft-cli", "/work/brand-mark.svg", "/work/brand-mark.outlined.svg",
            "path.booleanUnion", "Boolean Union & Outline Strokes", "{\"precision\":3,\"flattenCurves\":false}",
            [".svg", ".ai", ".eps", ".vcproj"],
            ["Artboard 1 · Primary Logo Vectors", "Grid & Guides (Locked)"], WebReleaseVersion: "0.22.0"),
        new("lightcraft", "LightCraft", "RAW Photo Development & Grading",
            "Lightroom-compatible non-destructive RAW photo catalog, exposure/color grading, LUTs, and batch export studio.",
            "Studio", "#38BDF8", "artcraft:lightcraft", 18085, 18103, "lightcraft-cli", "/work/shoot-01.dng", "/work/shoot-01.developed.jpg",
            "develop.autoTone", "Auto-Tone & Highlight Recovery", "{\"exposure\":0.35,\"highlights\":-42,\"shadows\":28,\"vibrance\":15}",
            [".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf"],
            ["RAW Sensor Master (16-bit Linear)", "Filmic Tone Curve + LUT"], WebReleaseVersion: "0.19.0"),
        new("effectcraft", "EffectCraft", "Motion Graphics & VFX Compositor",
            "After Effects-compatible node and layer motion graphics, keyframe animation, particle emitters, and VFX compositing studio.",
            "Studio", "#A855F7", "artcraft:effectcraft", 18086, 18104, "effectcraft-cli", "/work/intro-sting.ecproj", "/work/intro-sting.comp.mp4",
            "comp.renderPreview", "Composite Motion Keyframes & Glow Pass", "{\"fps\":60,\"motionBlur\":true,\"samples\":16}",
            [".ecproj", ".exr"],
            ["Comp 1 · Optical Glow Pass", "3D Camera Rig + Null Controller", "Background Plate"], WebReleaseVersion: "0.22.0"),
        new("designcraft", "DesignCraft", "Page Layout & Desktop Publishing",
            "InDesign-compatible multi-page layout, master spreads, baseline typography grids, and print-ready PDF/X publishing studio.",
            "Studio", "#EC4899", "artcraft:designcraft", 18087, 18105, "designcraft-cli", "/work/annual-report.dcproj", "/work/annual-report.press.pdf",
            "layout.preflightExport", "Preflight & Export Print-Ready Spreads", "{\"profile\":\"PDF/X-4\",\"bleedMm\":3}",
            [".dcproj", ".idml"],
            ["A-Master · Running Headers & Folio", "Spread 1-2 · Editorial Grid (12 Columns)"], WebReleaseVersion: "0.18.0"),
        new("pdfcraft", "PdfCraft", "PDF Editor, Organizer & Redactor",
            "Acrobat-compatible PDF reader, structural editor, page organizer, OCR text extractor, form filler, and cryptographic signer.",
            "Workspace", "#EF4444", "artcraft:pdfcraft", 18088, 18106, "pdfcraft-cli", "/work/contract.pdf", "/work/contract.annotated.pdf",
            "pdf.optimizeAndOcr", "Optimize PDF & Extract Searchable Text Layer", "{\"dpi\":300,\"compressImages\":true}",
            [".pdf"],
            ["Page Tree (12 Pages · PDF 2.0)", "Annotations & Signatures Layer"], WebReleaseVersion: "0.19.0"),
        new("wordcraft", "WordCraft", "Clean-Room Document & Word Processor",
            "Word-compatible structured document processor with semantic styles, citations, track changes, and DOCX/Markdown/PDF export.",
            "Workspace", "#2563EB", "artcraft:wordcraft", 18089, 18107, "wordcraft-cli", "/work/proposal.docx", "/work/proposal.final.docx",
            "document.formatStyles", "Normalize Headings & Generate Table of Contents", "{\"styleSet\":\"modern-editorial\",\"tocDepth\":3}",
            [".docx", ".doc", ".odt", ".rtf"],
            ["Section 1 · Executive Summary", "Section 2 · Architecture & Milestones"], WebReleaseVersion: "0.16.0"),
        new("soundcraft", "SoundCraft", "Digital Audio Workstation & Mastering",
            "Multi-track digital audio workstation (DAW) for spectral denoising, parametric EQ, stem mixing, and loudness normalization.",
            "Studio", "#10B981", "artcraft:soundcraft", 18090, 18108, "soundcraft-cli", "/work/podcast-ep1.scproj", "/work/podcast-ep1.master.wav",
            "audio.masterLufs", "Spectral Denoise & Normalize to -14 LUFS", "{\"targetLufs\":-14.0,\"truePeakDb\":-1.0}",
            [".wav", ".flac", ".mp3", ".ogg", ".m4a", ".scproj"],
            ["Track 1 · Host Vocal (De-Esser + Comp)", "Track 2 · Ambient Bed (-18 dB)", "Master Bus · Limiter (-1.0 dBTP)"], WebReleaseVersion: "0.18.0"),
        new("deckcraft", "DeckCraft", "Presentation & Slide Design Studio",
            "Keynote/PowerPoint-compatible slide deck studio with master layouts, smart transitions, speaker notes, and PPTX/PDF export.",
            "Workspace", "#F97316", "artcraft:deckcraft", 18091, 18109, "deckcraft-cli", "/work/q4-keynote.pptx", "/work/q4-keynote.export.pdf",
            "deck.applyMasterTheme", "Apply Master Layout & Align Slide Visuals", "{\"theme\":\"fluent-dark\",\"aspect\":\"16:9\"}",
            [".pptx", ".ppt", ".odp", ".key"],
            ["Slide 1 · Title Cover", "Slide 2 · Q4 Growth Metrics", "Slide 3 · Roadmap"], WebReleaseVersion: "0.16.0"),
        new("cadcraft", "CADCraft", "Parametric 2D/3D CAD & Drafting",
            "AutoCAD/FreeCAD-compatible precision parametric 2D drafting and 3D solid modeling studio with STEP/IGES/DXF/STL support.",
            "Studio", "#0EA5E9", "artcraft:cadcraft", 18092, 18112, "cadcraft-cli", "/work/enclosure.step", "/work/enclosure.mesh.stl",
            "cad.tessellateMesh", "Validate Manifold Solid & Export High-Res STL", "{\"linearDeflectionMm\":0.05,\"angularDeflectionDeg\":5}",
            [".step", ".stp", ".iges", ".igs", ".dxf", ".dwg", ".stl"],
            ["PartBody · CNC Aluminum Housing", "Sketch 01 · Mounting Holes (Fully Constrained)"], WebReleaseVersion: "0.18.0"),
        new("gridcraft", "GridCraft", "High-Performance Spreadsheet & Pivot Engine",
            "Excel-compatible reactive spreadsheet engine for formula evaluation, pivot tables, financial modeling, and CSV/XLSX/Parquet workflows.",
            "Workspace", "#16A34A", "artcraft:gridcraft", 18093, 18113, "gridcraft-cli", "/work/financial-model.xlsx", "/work/financial-model.recalc.xlsx",
            "sheet.recalculateAndPivot", "Recalculate Workbook Formulas & Refresh Pivot Summary", "{\"sheet\":\"Summary\",\"strictTypes\":true}",
            [".xlsx", ".xls", ".ods", ".tsv"],
            ["Sheet 1 · Revenue Forecast", "Sheet 2 · Cohort Pivot Table"], WebReleaseVersion: "0.17.0"),
        new("figcraft", "FigCraft", "Collaborative Interface & UX Prototyping",
            "Figma-compatible interface design tool with auto-layout frames, design tokens, component variants, and interactive prototypes.",
            "Studio", "#F43F5E", "artcraft:figcraft", 18094, 18114, "figcraft-cli", "/work/design-system.fig", "/work/design-system.tokens.json",
            "design.exportTokens", "Sync Auto-Layout Components & Export W3C Design Tokens", "{\"format\":\"w3c-dtcg\",\"includeVariants\":true}",
            [".fig", ".figcraft"],
            ["Page 1 · Design Tokens & Foundations", "Page 2 · Shell Components"], WebReleaseVersion: "0.16.0"),
        new("texturecraft", "TextureCraft", "3D PBR Texture Painting Studio",
            "Substance Painter-compatible 3D mesh texture painting studio with smart masks, curvature baking, and PBR channel export.",
            "Studio", "#D97706", "artcraft:texturecraft", 18095, 18115, "texturecraft-cli", "/work/helmet.tcproj", "/work/helmet.pbr-4k.png",
            "texture.bakeAndExportPbr", "Bake Ambient Occlusion & Export 4K PBR Channels", "{\"resolution\":4096,\"channels\":[\"albedo\",\"normal\",\"roughness\",\"metallic\"]}",
            [".tcproj", ".hdr", ".exr"],
            ["Smart Material · Brushed Titanium", "Wear & Edge Scratches (Curvature Mask)"], WebReleaseVersion: "0.4.0"),
        new("materialcraft", "MaterialCraft", "Procedural PBR Material Graph Authoring",
            "Substance Designer-compatible node-based procedural material generator for seamless tileable PBR surfaces.",
            "Studio", "#EA580C", "artcraft:materialcraft", 18096, 18116, "materialcraft-cli", "/work/nordic-granite.mcproj", "/work/nordic-granite.albedo.png",
            "material.evaluateGraph", "Evaluate Procedural Noise Graph & Render PBR Maps", "{\"seed\":42,\"resolution\":2048,\"tileable\":true}",
            [".mcproj", ".sbsar"],
            ["Voronoi Fracture Generator", "Height-to-Normal Sobel Filter", "PBR Metallic/Roughness Output"], WebReleaseVersion: "0.2.0"),
        new("solidcraft", "SolidCraft", "3D Polygonal Modeling, Sculpting & Scene Studio",
            "Blender-compatible 3D polygonal modeling, subdivision surfaces, UV unwrapping, and glTF/USDZ scene studio.",
            "Studio", "#14B8A6", "artcraft:solidcraft", 18097, 18117, "solidcraft-cli", "/work/product-scene.glb", "/work/product-scene.optimized.glb",
            "scene.subdivideAndExportGltf", "Apply Subdivision Surface & Export Draco glTF 2.0", "{\"subdivisionLevels\":2,\"dracoCompression\":true}",
            [".glb", ".gltf", ".obj", ".fbx", ".usdz", ".blend"],
            ["Collection 1 · Hero Mesh (Subdiv Lv2)", "Studio 3-Point Area Rig", "Physically Based Camera (50mm)"]),
    ];

    private static readonly FrozenDictionary<string, ArtCraftAppDescriptor> ByIdMap =
        All.ToFrozenDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, ArtCraftAppDescriptor> ById => ByIdMap;

    public static bool IsArtCraftApp(string? appId)
        => !string.IsNullOrWhiteSpace(appId) && ByIdMap.ContainsKey(appId.Trim());

    public static bool TryGet(string? appId, out ArtCraftAppDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(appId) && ByIdMap.TryGetValue(NormalizeAppId(appId), out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static ArtCraftAppDescriptor Get(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return TryGet(appId, out var descriptor)
            ? descriptor
            : throw new ArgumentException($"'{appId}' is not an ArtCraft app; supported apps: {string.Join(", ", All.Select(a => a.Id))}.", nameof(appId));
    }

    public static string NormalizeAppId(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "videocraft" => "filmcraft",
        "craft" or "craft-launcher" => "artcraft",
        var trimmed => trimmed,
    };

    public static ArtCraftAppDescriptor? FindByExtension(string? pathOrFileName)
    {
        var ext = string.IsNullOrWhiteSpace(pathOrFileName) ? null : Path.GetExtension(pathOrFileName);
        return string.IsNullOrWhiteSpace(ext)
            ? null
            : All.FirstOrDefault(app => app.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }
}
