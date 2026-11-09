/*
# ArtCraft Suite Apps
Role: Default
Shipped: true
Operations: open, edit, run-command, switch-mode

Unified, catalog-driven implementation for the getartcraft.com (storytold) open-source Rust creative and productivity suite:
- ArtCraft, VectorCraft, LightCraft, EffectCraft, DesignCraft, PdfCraft, WordCraft, SoundCraft, DeckCraft, CADCraft, GridCraft, FigCraft, TextureCraft, MaterialCraft, SolidCraft.
Each app supports:
1. Interactive WASM Web UI mode (UI.WebBrowser)
2. MCP Server mode (IArtCraftStudio + IMcpServer at {brain}/mcp/{appId})
3. Native Linux CLI mode (craft-cli / <app>-cli)
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Apps.ArtCraft;

[DigitalBrain.Signal("artcraft.studio.open")]
public sealed record ArtCraftStudioAppOpen(
    string? Document = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("artcraft.studio.edit")]
public sealed record ArtCraftStudioAppEdit(
    string? Prompt = null,
    string? Document = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("artcraft.studio.run-command")]
public sealed record ArtCraftStudioAppRunCommand(
    string? CommandId = null,
    string? ParametersJson = null,
    string? Document = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("artcraft.studio.switch-mode")]
public sealed record ArtCraftStudioAppSwitchMode(
    string? Mode = null) : DigitalBrain.Signal;

public sealed record ArtCraftStudioAppState(
    string AppId,
    string ExecutionMode,
    string ActiveDocument,
    string OutputDocument,
    string LastCommandId,
    string LastCommandTitle,
    string LastCliInvocation,
    string Status,
    string[] LayersOrTracks,
    string? WebUrl,
    string McpEndpointUri)
{
    public ArtCraftStudioAppState() : this(
        "artcraft",
        "wasm-ui",
        "/work/project.craft",
        "/work/project.export.png",
        "suite.inspect",
        "Inspect ArtCraft Workspace",
        "craft-cli run /work/project.craft --cmd suite.inspect",
        "ArtCraft tools connected.",
        ["ArtCraft Suite Hub · Connected"],
        "http://localhost:18080/",
        "neuron://local/mcp/artcraft")
    {
    }

    public static ArtCraftStudioAppState For(string appId)
    {
        var d = ArtCraftCatalog.Get(appId);
        return new ArtCraftStudioAppState(
            d.Id,
            "wasm-ui",
            d.DefaultDocument,
            d.DefaultOutputDocument,
            d.DefaultCommandId,
            $"Ready · {d.Title}",
            $"{d.CliBinary} run {d.DefaultDocument} --cmd {d.DefaultCommandId} --params '{d.DefaultParametersJson}' --out {d.DefaultOutputDocument}",
            $"{d.Title} tools connected.",
            d.DefaultLayersOrTracks,
            d.DefaultWebUrl,
            $"neuron://local/mcp/{d.Id}");
    }
}

public abstract class ArtCraftStudioAppCore<TState>(string appId) : DigitalBrain.App<TState>
    where TState : class, new()
{
    public ArtCraftAppDescriptor Descriptor { get; } = ArtCraftCatalog.Get(appId);

    public IFilesVfsService? FilesVfs { get; set; }

    protected virtual string ActiveDocumentLabel => "Active";

    protected async Task<(string Mode, string Document, string? WebUrl, string McpUri, string Status)> ResolveOpenSessionCoreAsync(
        string? requestedDocument,
        string? requestedMode,
        string currentMode,
        string currentDocument,
        string? currentWebUrl,
        string currentMcpUri)
    {
        var mode = string.IsNullOrWhiteSpace(requestedMode)
            ? currentMode
            : ArtCraftEngineRunner.NormalizeMode(requestedMode);
        var doc = string.IsNullOrWhiteSpace(requestedDocument)
            ? currentDocument
            : requestedDocument.Trim();
        var webUrl = currentWebUrl ?? Descriptor.DefaultWebUrl;
        var mcpUri = currentMcpUri;

        if (FilesVfs is not null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(requestedDocument))
                {
                    if (await FilesVfs.ResolveFileAsync(requestedDocument.Trim()) is { } resolvedExplicit)
                    {
                        doc = resolvedExplicit.VfsPath;
                    }
                }
                else if (await FilesVfs.ResolveFileAsync(doc) is { } resolvedCurrent)
                {
                    doc = resolvedCurrent.VfsPath;
                }
                else if (string.Equals(doc, Descriptor.DefaultDocument, StringComparison.OrdinalIgnoreCase))
                {
                    var files = await FilesVfs.ListFilesAsync();
                    if (files.FirstOrDefault(f => Descriptor.SupportedExtensions.Any(ext => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) is { } matched)
                    {
                        doc = matched.VfsPath;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, $"Files VFS resolution is optional when opening {Descriptor.Title}.");
            }
        }

        if (TryGetArtCraftStudio() is { } studio)
        {
            try
            {
                var session = await studio.Open(Descriptor.Id);
                mcpUri = session.McpKey;
                webUrl = session.WebUrl ?? webUrl;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, $"ArtCraftStudioNeuron MCP endpoint is optional when opening {Descriptor.Title} in local/headless mode.");
            }
        }

        return (mode, doc, webUrl, mcpUri, $"{Descriptor.Title} tools connected ({FormatModeLabel(mode)}) · {ActiveDocumentLabel}: {doc}");
    }

    protected async Task<ArtCraftEngineResult> ExecuteStudioCommandAsync(
        string instructionOrCommand,
        string? explicitParamsJson,
        string targetDoc,
        string mode)
    {
        ArtCraftEngineResult? result = null;
        if (FilesVfs is null && TryGetArtCraftEngine() is { } engine)
        {
            try
            {
                result = await engine.Execute(Descriptor.Id, instructionOrCommand, explicitParamsJson, targetDoc, mode);
            }
            catch (Exception error) when (error is not OperationCanceledException and not UnauthorizedAccessException)
            {
                Swallow(error, $"ArtCraftEngineNeuron is optional when running {Descriptor.Title} standalone.");
            }
        }

        return result ?? await ArtCraftEngineRunner.ExecuteAsync(
            Descriptor.Id,
            instructionOrCommand,
            explicitParamsJson,
            targetDoc,
            mode,
            TryGetArtCraftStudio(),
            TryGetMcpServer,
            (err, reason) => Swallow(err, reason),
            FilesVfs);
    }

    protected View RenderStudioView(string status, string activeDoc, string outputDoc, string? webUrl, string executionMode) => new(
        Descriptor.Title,
        new Column(
        [
            new Text($"{status} · {activeDoc} -> {outputDoc}") { Tone = "muted", Tabular = true },
            UI.WebBrowser(
                webUrl ?? Descriptor.DefaultWebUrl,
                $"{Descriptor.Title} WASM Studio",
                $"{Descriptor.Id}-wasm",
                0,
                executionMode),
        ])
        {
            Gap = 4,
            Extents = [24, 0],
        });

    protected string FormatModeLabel(string mode) => mode switch
    {
        "mcp" => $"MCP Server ({Descriptor.Id}-mcp)",
        "wasm-ui" => "WASM Web UI",
        _ => $"Native Linux CLI ({Descriptor.CliBinary})",
    };

    private IArtCraftEngine? TryGetArtCraftEngine()
    {
        try { return Get<IArtCraftEngine>("apps/artcraft-engine"); }
        catch (InvalidOperationException error)
        {
            Swallow(error, $"ArtCraftEngineNeuron is optional when running {Descriptor.Title} standalone.");
            return null;
        }
    }

    private IArtCraftStudio? TryGetArtCraftStudio()
    {
        try { return Get<IArtCraftStudio>("artcraft"); }
        catch (InvalidOperationException error)
        {
            Swallow(error, $"ArtCraftStudioNeuron is optional when running {Descriptor.Title} in unit tests.");
            return null;
        }
    }

    private IMcpServer? TryGetMcpServer(string mcpKey)
    {
        try { return Get<IMcpServer>(mcpKey); }
        catch (InvalidOperationException error)
        {
            Swallow(error, $"McpServerNeuron is optional when running {Descriptor.Title} in unit tests.");
            return null;
        }
    }
}

public abstract class ArtCraftStudioAppBase : ArtCraftStudioAppCore<ArtCraftStudioAppState>
{
    protected ArtCraftStudioAppBase(string appId) : base(appId)
    {
        Set(ArtCraftStudioAppState.For(Descriptor.Id));
    }

    protected static AppBundle CreateBundle(string appId, Type appType)
    {
        var d = ArtCraftCatalog.Get(appId);
        return new AppBundle(
            d.Id,
            "1.1.0",
            new AppManifest(
                Title: d.Title,
                Description: d.Description,
                Operations:
                [
                    new("open", $"Connect {d.Title} studio and open or inspect a document."),
                    new("edit", $"Edit a {d.Title} document from natural language or command instructions."),
                    new("run-command", $"Execute a {d.Title} command ID directly via Native Linux CLI or MCP."),
                    new("switch-mode", "Switch execution backend between native-linux, mcp, and wasm-ui."),
                ],
                Settings: [],
                Slots: [],
                Icon: "icon.svg",
                Toolbar:
                [
                    new("open", Icon: "link", Label: "Connect studio"),
                    new("run-command", Icon: d.Icon, Label: d.DefaultCommandTitle),
                ]),
            "",
            new Dictionary<string, string>(StringComparer.Ordinal))
        {
            CompiledType = appType.AssemblyQualifiedName,
        };
    }

    [DigitalBrain.On]
    public async Task Open(ArtCraftStudioAppOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (mode, doc, webUrl, mcpUri, status) = await ResolveOpenSessionCoreAsync(
            request.Document,
            request.Mode,
            State.ExecutionMode,
            State.ActiveDocument,
            State.WebUrl,
            State.McpEndpointUri);
        Set(State with
        {
            AppId = Descriptor.Id,
            ExecutionMode = mode,
            ActiveDocument = doc,
            WebUrl = webUrl,
            McpEndpointUri = mcpUri,
            Status = status,
        });
    }

    [DigitalBrain.On]
    public async Task Edit(ArtCraftStudioAppEdit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = string.IsNullOrWhiteSpace(request.Prompt)
            ? Descriptor.DefaultCommandId
            : request.Prompt.Trim();
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var targetDoc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : request.Document.Trim();

        await ApplyExecutionAsync(prompt, explicitParamsJson: null, targetDoc, mode);
    }

    [DigitalBrain.On]
    public async Task RunCommand(ArtCraftStudioAppRunCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cmdId = string.IsNullOrWhiteSpace(request.CommandId)
            ? Descriptor.DefaultCommandId
            : request.CommandId.Trim();
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var targetDoc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : request.Document.Trim();

        await ApplyExecutionAsync(cmdId, request.ParametersJson, targetDoc, mode);
    }

    [DigitalBrain.On]
    public void SwitchMode(ArtCraftStudioAppSwitchMode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextMode = ArtCraftEngineRunner.NormalizeMode(request.Mode);
        Set(State with
        {
            ExecutionMode = nextMode,
            Status = $"Switched {Descriptor.Title} execution backend to {FormatModeLabel(nextMode)}",
        });
    }

    public View? RenderSide() => null;

    public View Render() => RenderStudioView(State.Status, State.ActiveDocument, State.OutputDocument, State.WebUrl, State.ExecutionMode);

    protected async Task ApplyExecutionAsync(
        string instructionOrCommand,
        string? explicitParamsJson,
        string targetDoc,
        string mode)
    {
        var result = await ExecuteStudioCommandAsync(instructionOrCommand, explicitParamsJson, targetDoc, mode);
        if (!result.IsValidCommand)
        {
            Set(State with
            {
                ExecutionMode = result.ExecutionMode,
                LastCommandId = result.CommandId,
                LastCommandTitle = result.CommandTitle,
                LastCliInvocation = string.Empty,
                Status = result.Summary,
            });
            return;
        }

        Set(State with
        {
            ExecutionMode = result.ExecutionMode,
            ActiveDocument = result.InputDocument,
            OutputDocument = result.OutputDocument,
            LastCommandId = result.CommandId,
            LastCommandTitle = result.CommandTitle,
            LastCliInvocation = result.CliInvocation,
            Status = result.Summary,
            LayersOrTracks = result.LayersOrTracks,
        });
    }
}

[DigitalBrain.App("artcraft", Accent = "#6366F1", Category = "Studio")]
public sealed class ArtCraftApp() : ArtCraftStudioAppBase("artcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(ArtCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("artcraft", typeof(ArtCraftApp));
}

[DigitalBrain.App("vectorcraft", Accent = "#F59E0B", Category = "Studio")]
public sealed class VectorCraftApp() : ArtCraftStudioAppBase("vectorcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(VectorCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("vectorcraft", typeof(VectorCraftApp));
}

[DigitalBrain.App("lightcraft", Accent = "#38BDF8", Category = "Studio")]
public sealed class LightCraftApp() : ArtCraftStudioAppBase("lightcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(LightCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("lightcraft", typeof(LightCraftApp));
}

[DigitalBrain.App("effectcraft", Accent = "#A855F7", Category = "Studio")]
public sealed class EffectCraftApp() : ArtCraftStudioAppBase("effectcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(EffectCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("effectcraft", typeof(EffectCraftApp));
}

[DigitalBrain.App("designcraft", Accent = "#EC4899", Category = "Studio")]
public sealed class DesignCraftApp() : ArtCraftStudioAppBase("designcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(DesignCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("designcraft", typeof(DesignCraftApp));
}

[DigitalBrain.App("pdfcraft", Accent = "#EF4444", Category = "Workspace")]
public sealed class PdfCraftApp() : ArtCraftStudioAppBase("pdfcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(PdfCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("pdfcraft", typeof(PdfCraftApp));
}

[DigitalBrain.App("wordcraft", Accent = "#2563EB", Category = "Workspace")]
public sealed class WordCraftApp() : ArtCraftStudioAppBase("wordcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(WordCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("wordcraft", typeof(WordCraftApp));
}

[DigitalBrain.App("soundcraft", Accent = "#10B981", Category = "Studio")]
public sealed class SoundCraftApp() : ArtCraftStudioAppBase("soundcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(SoundCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("soundcraft", typeof(SoundCraftApp));
}

[DigitalBrain.App("deckcraft", Accent = "#F97316", Category = "Workspace")]
public sealed class DeckCraftApp() : ArtCraftStudioAppBase("deckcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(DeckCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("deckcraft", typeof(DeckCraftApp));
}

[DigitalBrain.App("cadcraft", Accent = "#0EA5E9", Category = "Studio")]
public sealed class CadCraftApp() : ArtCraftStudioAppBase("cadcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(CadCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("cadcraft", typeof(CadCraftApp));
}

[DigitalBrain.App("gridcraft", Accent = "#16A34A", Category = "Workspace")]
public sealed class GridCraftApp() : ArtCraftStudioAppBase("gridcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(GridCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("gridcraft", typeof(GridCraftApp));
}

[DigitalBrain.App("figcraft", Accent = "#F43F5E", Category = "Studio")]
public sealed class FigCraftApp() : ArtCraftStudioAppBase("figcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(FigCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("figcraft", typeof(FigCraftApp));
}

[DigitalBrain.App("texturecraft", Accent = "#D97706", Category = "Studio")]
public sealed class TextureCraftApp() : ArtCraftStudioAppBase("texturecraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(TextureCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("texturecraft", typeof(TextureCraftApp));
}

[DigitalBrain.App("materialcraft", Accent = "#EA580C", Category = "Studio")]
public sealed class MaterialCraftApp() : ArtCraftStudioAppBase("materialcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(MaterialCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("materialcraft", typeof(MaterialCraftApp));
}

[DigitalBrain.App("solidcraft", Accent = "#14B8A6", Category = "Studio")]
public sealed class SolidCraftApp() : ArtCraftStudioAppBase("solidcraft")
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(SolidCraftApp));
    public static AppBundle Bundle { get; } = CreateBundle("solidcraft", typeof(SolidCraftApp));
}
