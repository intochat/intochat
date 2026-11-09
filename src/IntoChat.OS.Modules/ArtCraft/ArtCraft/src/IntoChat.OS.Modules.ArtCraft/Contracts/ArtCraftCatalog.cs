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
        new(
            Id: "artcraft",
            Title: "ArtCraft",
            Subtitle: "Unified Creative Suite & Launcher",
            Description: "Master creative suite hub and launcher for the open-source Rust ArtCraft ecosystem (WASM Web UI, MCP Server, and craft-cli).",
            Category: "Studio",
            AccentHex: "#6366F1",
            Icon: "sparkles",
            DefaultWebPort: 18080,
            DefaultMcpPort: 18098,
            CliBinary: "craft-cli",
            DefaultDocument: "/work/project.craft",
            DefaultOutputDocument: "/work/project.export.png",
            DefaultCommandId: "suite.inspect",
            DefaultCommandTitle: "Inspect ArtCraft Workspace",
            DefaultParametersJson: "{\"suite\":\"artcraft\"}",
            SupportedExtensions: [".craft"],
            DefaultLayersOrTracks: ["ArtCraft Suite Hub · Connected", "Shared /work Volume · Mounted"]),
        new(
            Id: "photocraft",
            Title: "PhotoCraft",
            Subtitle: "Layered Raster & Photo Studio",
            Description: "Photoshop-compatible layered image studio for editing images, removing backgrounds, adjusting levels, and generating graphics.",
            Category: "Studio",
            AccentHex: "#06B6D4",
            Icon: "image",
            DefaultWebPort: 18082,
            DefaultMcpPort: 18100,
            CliBinary: "photocraft-cli",
            DefaultDocument: "/work/portrait.png",
            DefaultOutputDocument: "/work/portrait.nobg.png",
            DefaultCommandId: "layer.removeBackground",
            DefaultCommandTitle: "Remove Background (AI Subject Alpha Mask)",
            DefaultParametersJson: "{\"featherPx\":1,\"output\":\"layer-mask\"}",
            SupportedExtensions: [".png", ".jpg", ".jpeg", ".webp", ".psd", ".avif", ".gif", ".tiff"],
            DefaultLayersOrTracks: ["Subject Layer (100% · Normal)", "Background Layer (Locked · 100%)"],
            WebReleaseVersion: "0.21.0"),
        new(
            Id: "filmcraft",
            Title: "FilmCraft",
            Subtitle: "Non-Linear Video Editor",
            Description: "Premiere-compatible non-linear video editor (VideoCraft / FilmCraft) with full-card WASM Web UI, Native Linux CLI (filmcraft-cli), and MCP Server.",
            Category: "Studio",
            AccentHex: "#8B5CF6",
            Icon: "film",
            DefaultWebPort: 18083,
            DefaultMcpPort: 18101,
            CliBinary: "filmcraft-cli",
            DefaultDocument: "/work/launch-trailer.fcproj",
            DefaultOutputDocument: "/work/launch-trailer.cut.mp4",
            DefaultCommandId: "timeline.razor",
            DefaultCommandTitle: "Razor Split Clip at Playhead (00:00:03:12)",
            DefaultParametersJson: "{\"timecode\":\"00:00:03:12\",\"track\":\"V1\"}",
            SupportedExtensions: [".mp4", ".mov", ".fcproj", ".mkv", ".webm"],
            DefaultLayersOrTracks: [
                "V2 · Title Overlay (00:00:00:00 -> 00:00:05:00)",
                "V1 · A-Roll Main Camera (00:00:00:00 -> 00:00:14:00)",
                "A1 · Master Dialogue Bus (-14 LUFS)"
            ],
            WebReleaseVersion: "0.18.0"),
        new(
            Id: "vectorcraft",
            Title: "VectorCraft",
            Subtitle: "Vector Illustration & Bézier Studio",
            Description: "Illustrator-compatible vector design studio with Bézier anchors, boolean pathfinder operations, live blends, and SVG/PDF export.",
            Category: "Studio",
            AccentHex: "#F59E0B",
            Icon: "pen-tool",
            DefaultWebPort: 18084,
            DefaultMcpPort: 18102,
            CliBinary: "vectorcraft-cli",
            DefaultDocument: "/work/brand-mark.svg",
            DefaultOutputDocument: "/work/brand-mark.outlined.svg",
            DefaultCommandId: "path.booleanUnion",
            DefaultCommandTitle: "Boolean Union & Outline Strokes",
            DefaultParametersJson: "{\"precision\":3,\"flattenCurves\":false}",
            SupportedExtensions: [".svg", ".ai", ".eps", ".vcproj"],
            DefaultLayersOrTracks: ["Artboard 1 · Primary Logo Vectors", "Grid & Guides (Locked)"],
            WebReleaseVersion: "0.22.0"),
        new(
            Id: "lightcraft",
            Title: "LightCraft",
            Subtitle: "RAW Photo Development & Grading",
            Description: "Lightroom-compatible non-destructive RAW photo catalog, exposure/color grading, LUTs, and batch export studio.",
            Category: "Studio",
            AccentHex: "#38BDF8",
            Icon: "sun",
            DefaultWebPort: 18085,
            DefaultMcpPort: 18103,
            CliBinary: "lightcraft-cli",
            DefaultDocument: "/work/shoot-01.dng",
            DefaultOutputDocument: "/work/shoot-01.developed.jpg",
            DefaultCommandId: "develop.autoTone",
            DefaultCommandTitle: "Auto-Tone & Highlight Recovery",
            DefaultParametersJson: "{\"exposure\":0.35,\"highlights\":-42,\"shadows\":28,\"vibrance\":15}",
            SupportedExtensions: [".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf"],
            DefaultLayersOrTracks: ["RAW Sensor Master (16-bit Linear)", "Filmic Tone Curve + LUT"],
            WebReleaseVersion: "0.19.0"),
        new(
            Id: "effectcraft",
            Title: "EffectCraft",
            Subtitle: "Motion Graphics & VFX Compositor",
            Description: "After Effects-compatible node and layer motion graphics, keyframe animation, particle emitters, and VFX compositing studio.",
            Category: "Studio",
            AccentHex: "#A855F7",
            Icon: "wand-sparkles",
            DefaultWebPort: 18086,
            DefaultMcpPort: 18104,
            CliBinary: "effectcraft-cli",
            DefaultDocument: "/work/intro-sting.ecproj",
            DefaultOutputDocument: "/work/intro-sting.comp.mp4",
            DefaultCommandId: "comp.renderPreview",
            DefaultCommandTitle: "Composite Motion Keyframes & Glow Pass",
            DefaultParametersJson: "{\"fps\":60,\"motionBlur\":true,\"samples\":16}",
            SupportedExtensions: [".ecproj", ".exr"],
            DefaultLayersOrTracks: ["Comp 1 · Optical Glow Pass", "3D Camera Rig + Null Controller", "Background Plate"],
            WebReleaseVersion: "0.22.0"),
        new(
            Id: "designcraft",
            Title: "DesignCraft",
            Subtitle: "Page Layout & Desktop Publishing",
            Description: "InDesign-compatible multi-page layout, master spreads, baseline typography grids, and print-ready PDF/X publishing studio.",
            Category: "Studio",
            AccentHex: "#EC4899",
            Icon: "layout",
            DefaultWebPort: 18087,
            DefaultMcpPort: 18105,
            CliBinary: "designcraft-cli",
            DefaultDocument: "/work/annual-report.dcproj",
            DefaultOutputDocument: "/work/annual-report.press.pdf",
            DefaultCommandId: "layout.preflightExport",
            DefaultCommandTitle: "Preflight & Export Print-Ready Spreads",
            DefaultParametersJson: "{\"profile\":\"PDF/X-4\",\"bleedMm\":3}",
            SupportedExtensions: [".dcproj", ".idml"],
            DefaultLayersOrTracks: ["A-Master · Running Headers & Folio", "Spread 1-2 · Editorial Grid (12 Columns)"],
            WebReleaseVersion: "0.18.0"),
        new(
            Id: "pdfcraft",
            Title: "PdfCraft",
            Subtitle: "PDF Editor, Organizer & Redactor",
            Description: "Acrobat-compatible PDF reader, structural editor, page organizer, OCR text extractor, form filler, and cryptographic signer.",
            Category: "Workspace",
            AccentHex: "#EF4444",
            Icon: "file-text",
            DefaultWebPort: 18088,
            DefaultMcpPort: 18106,
            CliBinary: "pdfcraft-cli",
            DefaultDocument: "/work/contract.pdf",
            DefaultOutputDocument: "/work/contract.annotated.pdf",
            DefaultCommandId: "pdf.optimizeAndOcr",
            DefaultCommandTitle: "Optimize PDF & Extract Searchable Text Layer",
            DefaultParametersJson: "{\"dpi\":300,\"compressImages\":true}",
            SupportedExtensions: [".pdf"],
            DefaultLayersOrTracks: ["Page Tree (12 Pages · PDF 2.0)", "Annotations & Signatures Layer"],
            WebReleaseVersion: "0.19.0"),
        new(
            Id: "wordcraft",
            Title: "WordCraft",
            Subtitle: "Clean-Room Document & Word Processor",
            Description: "Word-compatible structured document processor with semantic styles, citations, track changes, and DOCX/Markdown/PDF export.",
            Category: "Workspace",
            AccentHex: "#2563EB",
            Icon: "file-text",
            DefaultWebPort: 18089,
            DefaultMcpPort: 18107,
            CliBinary: "wordcraft-cli",
            DefaultDocument: "/work/proposal.docx",
            DefaultOutputDocument: "/work/proposal.final.docx",
            DefaultCommandId: "document.formatStyles",
            DefaultCommandTitle: "Normalize Headings & Generate Table of Contents",
            DefaultParametersJson: "{\"styleSet\":\"modern-editorial\",\"tocDepth\":3}",
            SupportedExtensions: [".docx", ".doc", ".odt", ".rtf"],
            DefaultLayersOrTracks: ["Section 1 · Executive Summary", "Section 2 · Architecture & Milestones"],
            WebReleaseVersion: "0.16.0"),
        new(
            Id: "soundcraft",
            Title: "SoundCraft",
            Subtitle: "Digital Audio Workstation & Mastering",
            Description: "Multi-track digital audio workstation (DAW) for spectral denoising, parametric EQ, stem mixing, and loudness normalization.",
            Category: "Studio",
            AccentHex: "#10B981",
            Icon: "music",
            DefaultWebPort: 18090,
            DefaultMcpPort: 18108,
            CliBinary: "soundcraft-cli",
            DefaultDocument: "/work/podcast-ep1.scproj",
            DefaultOutputDocument: "/work/podcast-ep1.master.wav",
            DefaultCommandId: "audio.masterLufs",
            DefaultCommandTitle: "Spectral Denoise & Normalize to -14 LUFS",
            DefaultParametersJson: "{\"targetLufs\":-14.0,\"truePeakDb\":-1.0}",
            SupportedExtensions: [".wav", ".flac", ".mp3", ".ogg", ".m4a", ".scproj"],
            DefaultLayersOrTracks: ["Track 1 · Host Vocal (De-Esser + Comp)", "Track 2 · Ambient Bed (-18 dB)", "Master Bus · Limiter (-1.0 dBTP)"],
            WebReleaseVersion: "0.18.0"),
        new(
            Id: "deckcraft",
            Title: "DeckCraft",
            Subtitle: "Presentation & Slide Design Studio",
            Description: "Keynote/PowerPoint-compatible slide deck studio with master layouts, smart transitions, speaker notes, and PPTX/PDF export.",
            Category: "Workspace",
            AccentHex: "#F97316",
            Icon: "presentation",
            DefaultWebPort: 18091,
            DefaultMcpPort: 18109,
            CliBinary: "deckcraft-cli",
            DefaultDocument: "/work/q4-keynote.pptx",
            DefaultOutputDocument: "/work/q4-keynote.export.pdf",
            DefaultCommandId: "deck.applyMasterTheme",
            DefaultCommandTitle: "Apply Master Layout & Align Slide Visuals",
            DefaultParametersJson: "{\"theme\":\"nordlys-dark\",\"aspect\":\"16:9\"}",
            SupportedExtensions: [".pptx", ".ppt", ".odp", ".key"],
            DefaultLayersOrTracks: ["Slide 1 · Title Cover", "Slide 2 · Q4 Growth Metrics", "Slide 3 · Roadmap"],
            WebReleaseVersion: "0.16.0"),
        new(
            Id: "cadcraft",
            Title: "CADCraft",
            Subtitle: "Parametric 2D/3D CAD & Drafting",
            Description: "AutoCAD/FreeCAD-compatible precision parametric 2D drafting and 3D solid modeling studio with STEP/IGES/DXF/STL support.",
            Category: "Studio",
            AccentHex: "#0EA5E9",
            Icon: "box",
            DefaultWebPort: 18092,
            DefaultMcpPort: 18112,
            CliBinary: "cadcraft-cli",
            DefaultDocument: "/work/enclosure.step",
            DefaultOutputDocument: "/work/enclosure.mesh.stl",
            DefaultCommandId: "cad.tessellateMesh",
            DefaultCommandTitle: "Validate Manifold Solid & Export High-Res STL",
            DefaultParametersJson: "{\"linearDeflectionMm\":0.05,\"angularDeflectionDeg\":5}",
            SupportedExtensions: [".step", ".stp", ".iges", ".igs", ".dxf", ".dwg", ".stl"],
            DefaultLayersOrTracks: ["PartBody · CNC Aluminum Housing", "Sketch 01 · Mounting Holes (Fully Constrained)"],
            WebReleaseVersion: "0.18.0"),
        new(
            Id: "gridcraft",
            Title: "GridCraft",
            Subtitle: "High-Performance Spreadsheet & Pivot Engine",
            Description: "Excel-compatible reactive spreadsheet engine for formula evaluation, pivot tables, financial modeling, and CSV/XLSX/Parquet workflows.",
            Category: "Workspace",
            AccentHex: "#16A34A",
            Icon: "table",
            DefaultWebPort: 18093,
            DefaultMcpPort: 18113,
            CliBinary: "gridcraft-cli",
            DefaultDocument: "/work/financial-model.xlsx",
            DefaultOutputDocument: "/work/financial-model.recalc.xlsx",
            DefaultCommandId: "sheet.recalculateAndPivot",
            DefaultCommandTitle: "Recalculate Workbook Formulas & Refresh Pivot Summary",
            DefaultParametersJson: "{\"sheet\":\"Summary\",\"strictTypes\":true}",
            SupportedExtensions: [".xlsx", ".xls", ".ods", ".tsv"],
            DefaultLayersOrTracks: ["Sheet 1 · Revenue Forecast", "Sheet 2 · Cohort Pivot Table"],
            WebReleaseVersion: "0.17.0"),
        new(
            Id: "figcraft",
            Title: "FigCraft",
            Subtitle: "Collaborative Interface & UX Prototyping",
            Description: "Figma-compatible interface design tool with auto-layout frames, design tokens, component variants, and interactive prototypes.",
            Category: "Studio",
            AccentHex: "#F43F5E",
            Icon: "figma",
            DefaultWebPort: 18094,
            DefaultMcpPort: 18114,
            CliBinary: "figcraft-cli",
            DefaultDocument: "/work/design-system.fig",
            DefaultOutputDocument: "/work/design-system.tokens.json",
            DefaultCommandId: "design.exportTokens",
            DefaultCommandTitle: "Sync Auto-Layout Components & Export W3C Design Tokens",
            DefaultParametersJson: "{\"format\":\"w3c-dtcg\",\"includeVariants\":true}",
            SupportedExtensions: [".fig", ".figcraft"],
            DefaultLayersOrTracks: ["Page 1 · Design Tokens & Foundations", "Page 2 · Shell Components"],
            WebReleaseVersion: "0.16.0"),
        new(
            Id: "texturecraft",
            Title: "TextureCraft",
            Subtitle: "3D PBR Texture Painting Studio",
            Description: "Substance Painter-compatible 3D mesh texture painting studio with smart masks, curvature baking, and PBR channel export.",
            Category: "Studio",
            AccentHex: "#D97706",
            Icon: "paintbrush",
            DefaultWebPort: 18095,
            DefaultMcpPort: 18115,
            CliBinary: "texturecraft-cli",
            DefaultDocument: "/work/helmet.tcproj",
            DefaultOutputDocument: "/work/helmet.pbr-4k.png",
            DefaultCommandId: "texture.bakeAndExportPbr",
            DefaultCommandTitle: "Bake Ambient Occlusion & Export 4K PBR Channels",
            DefaultParametersJson: "{\"resolution\":4096,\"channels\":[\"albedo\",\"normal\",\"roughness\",\"metallic\"]}",
            SupportedExtensions: [".tcproj", ".hdr", ".exr"],
            DefaultLayersOrTracks: ["Smart Material · Brushed Titanium", "Wear & Edge Scratches (Curvature Mask)"],
            WebReleaseVersion: "0.4.0"),
        new(
            Id: "materialcraft",
            Title: "MaterialCraft",
            Subtitle: "Procedural PBR Material Graph Authoring",
            Description: "Substance Designer-compatible node-based procedural material generator for seamless tileable PBR surfaces.",
            Category: "Studio",
            AccentHex: "#EA580C",
            Icon: "layers",
            DefaultWebPort: 18096,
            DefaultMcpPort: 18116,
            CliBinary: "materialcraft-cli",
            DefaultDocument: "/work/nordic-granite.mcproj",
            DefaultOutputDocument: "/work/nordic-granite.albedo.png",
            DefaultCommandId: "material.evaluateGraph",
            DefaultCommandTitle: "Evaluate Procedural Noise Graph & Render PBR Maps",
            DefaultParametersJson: "{\"seed\":42,\"resolution\":2048,\"tileable\":true}",
            SupportedExtensions: [".mcproj", ".sbsar"],
            DefaultLayersOrTracks: ["Voronoi Fracture Generator", "Height-to-Normal Sobel Filter", "PBR Metallic/Roughness Output"],
            WebReleaseVersion: "0.2.0"),
        new(
            Id: "solidcraft",
            Title: "SolidCraft",
            Subtitle: "3D Polygonal Modeling, Sculpting & Scene Studio",
            Description: "Blender-compatible 3D polygonal modeling, subdivision surfaces, UV unwrapping, and glTF/USDZ scene studio.",
            Category: "Studio",
            AccentHex: "#14B8A6",
            Icon: "boxes",
            DefaultWebPort: 18097,
            DefaultMcpPort: 18117,
            CliBinary: "solidcraft-cli",
            DefaultDocument: "/work/product-scene.glb",
            DefaultOutputDocument: "/work/product-scene.optimized.glb",
            DefaultCommandId: "scene.subdivideAndExportGltf",
            DefaultCommandTitle: "Apply Subdivision Surface & Export Draco glTF 2.0",
            DefaultParametersJson: "{\"subdivisionLevels\":2,\"dracoCompression\":true}",
            SupportedExtensions: [".glb", ".gltf", ".obj", ".fbx", ".usdz", ".blend"],
            DefaultLayersOrTracks: ["Collection 1 · Hero Mesh (Subdiv Lv2)", "Studio 3-Point Area Rig", "Physically Based Camera (50mm)"]),
    ];

    private static readonly FrozenDictionary<string, ArtCraftAppDescriptor> ByIdMap =
        All.ToFrozenDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, ArtCraftAppDescriptor> ById => ByIdMap;

    public static bool IsArtCraftApp(string? appId)
        => !string.IsNullOrWhiteSpace(appId) && ByIdMap.ContainsKey(appId.Trim());

    public static bool TryGet(string? appId, out ArtCraftAppDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(appId))
        {
            var normalized = NormalizeAppId(appId);
            if (ByIdMap.TryGetValue(normalized, out var found))
            {
                descriptor = found;
                return true;
            }
        }

        descriptor = null!;
        return false;
    }

    public static ArtCraftAppDescriptor Get(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (TryGet(appId, out var descriptor))
        {
            return descriptor;
        }

        throw new ArgumentException(
            $"'{appId}' is not an ArtCraft app; supported apps: {string.Join(", ", All.Select(a => a.Id))}.",
            nameof(appId));
    }

    public static string NormalizeAppId(string raw)
    {
        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "videocraft" => "filmcraft",
            "craft" or "craft-launcher" => "artcraft",
            _ => trimmed,
        };
    }

    public static ArtCraftAppDescriptor? FindByExtension(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
        {
            return null;
        }

        var ext = Path.GetExtension(pathOrFileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            return null;
        }

        foreach (var app in All)
        {
            if (app.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                return app;
            }
        }

        return null;
    }
}
