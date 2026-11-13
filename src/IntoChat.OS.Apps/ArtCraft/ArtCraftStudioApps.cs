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
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Apps.ArtCraft;

[Signal("artcraft.studio.open")]
public sealed record ArtCraftStudioAppOpen(string? Document = null, string? Mode = null) : Signal;

[Signal("artcraft.studio.edit")]
public sealed record ArtCraftStudioAppEdit(string? Prompt = null, string? Document = null, string? Mode = null) : Signal;

[Signal("artcraft.studio.run-command")]
public sealed record ArtCraftStudioAppRunCommand(string? CommandId = null, string? ParametersJson = null, string? Document = null, string? Mode = null) : Signal;

[Signal("artcraft.studio.switch-mode")]
public sealed record ArtCraftStudioAppSwitchMode(string? Mode = null) : Signal;

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

public abstract class ArtCraftStudioAppCore<TState>(string appId) : Screen<TState>, IWithFilesVfs
    where TState : class, new()
{
    public new TState State => base.State;
    public View Render() => Render(State);

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
        var mode = NormalizeModeOrCurrent(requestedMode, currentMode);
        var doc = NormalizeTextOrCurrent(requestedDocument, currentDocument);
        var webUrl = currentWebUrl ?? Descriptor.DefaultWebUrl;
        var mcpUri = currentMcpUri;

        if (FilesVfs is not null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(requestedDocument))
                {
                    if (await FilesVfs.ResolveFileAsync(requestedDocument.Trim()) is { } resolvedExplicit) { doc = resolvedExplicit.VfsPath; }
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
                NullLogger.Instance.LogSwallowed(error, $"Files VFS resolution is optional when opening {Descriptor.Title}.");
            }
        }

        if (TryGetOptional<IArtCraftStudio>("artcraft", $"ArtCraftStudioNeuron is optional when running {Descriptor.Title} in unit tests.") is { } studio)
        {
            try
            {
                var session = await studio.Open(Descriptor.Id);
                mcpUri = session.McpKey;
                webUrl = session.WebUrl ?? webUrl;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                NullLogger.Instance.LogSwallowed(error, $"ArtCraftStudioNeuron MCP endpoint is optional when opening {Descriptor.Title} in local/headless mode.");
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
        if (FilesVfs is null && TryGetOptional<IArtCraftEngine>("apps/artcraft-engine", $"ArtCraftEngineNeuron is optional when running {Descriptor.Title} standalone.") is { } engine)
        {
            try { result = await engine.Execute(Descriptor.Id, instructionOrCommand, explicitParamsJson, targetDoc, mode); }
            catch (Exception error) when (error is not OperationCanceledException and not UnauthorizedAccessException)
            {
                NullLogger.Instance.LogSwallowed(error, $"ArtCraftEngineNeuron is optional when running {Descriptor.Title} standalone.");
            }
        }

        return result ?? await ArtCraftEngineRunner.ExecuteAsync(
            Descriptor.Id,
            instructionOrCommand,
            explicitParamsJson,
            targetDoc,
            mode,
            TryGetOptional<IArtCraftStudio>("artcraft", $"ArtCraftStudioNeuron is optional when running {Descriptor.Title} in unit tests."),
            mcpKey => TryGetOptional<IMcpServer>(mcpKey, $"McpServerNeuron is optional when running {Descriptor.Title} in unit tests."),
            (err, reason) => NullLogger.Instance.LogSwallowed(err, reason),
            FilesVfs);
    }

    protected View RenderStudioView(string status, string activeDoc, string outputDoc, string? webUrl, string executionMode) => new(
        Descriptor.Title,
        new Column(
        [
            new Text($"{status} · {activeDoc} -> {outputDoc}") { Tone = "muted", Tabular = true },
            UI.WebBrowser(webUrl ?? Descriptor.DefaultWebUrl, $"{Descriptor.Title} WASM Studio", $"{Descriptor.Id}-wasm", 0, executionMode),
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

    protected static string NormalizeModeOrCurrent(string? requestedMode, string currentMode)
        => string.IsNullOrWhiteSpace(requestedMode) ? currentMode : ArtCraftEngineRunner.NormalizeMode(requestedMode);

    protected static string NormalizeTextOrCurrent(string? requested, string fallback)
        => string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();

    private T? TryGetOptional<T>(string key, string reason) where T : class
    {
        try { return Get<T>(key); }
        catch (InvalidOperationException error)
        {
            NullLogger.Instance.LogSwallowed(error, reason);
            return null;
        }
    }
}

public abstract class ArtCraftStudioAppBase : ArtCraftStudioAppCore<ArtCraftStudioAppState>
{
    protected ArtCraftStudioAppBase(string appId) : base(appId)
    {
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        if (State.AppId != Descriptor.Id)
        {
            await Show(ArtCraftStudioAppState.For(Descriptor.Id));
        }
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

    [On]
    public async Task Open(ArtCraftStudioAppOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (mode, doc, webUrl, mcpUri, status) = await ResolveOpenSessionCoreAsync(
            request.Document, request.Mode, State.ExecutionMode, State.ActiveDocument, State.WebUrl, State.McpEndpointUri);
        await Show(State with
        {
            AppId = Descriptor.Id,
            ExecutionMode = mode,
            ActiveDocument = doc,
            WebUrl = webUrl,
            McpEndpointUri = mcpUri,
            Status = status,
        });
    }

    [On]
    public Task Edit(ArtCraftStudioAppEdit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyExecutionAsync(
            NormalizeTextOrCurrent(request.Prompt, Descriptor.DefaultCommandId),
            explicitParamsJson: null,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument),
            NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task RunCommand(ArtCraftStudioAppRunCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyExecutionAsync(
            NormalizeTextOrCurrent(request.CommandId, Descriptor.DefaultCommandId),
            request.ParametersJson,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument),
            NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task SwitchMode(ArtCraftStudioAppSwitchMode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextMode = ArtCraftEngineRunner.NormalizeMode(request.Mode);
        return Show(State with
        {
            ExecutionMode = nextMode,
            Status = $"Switched {Descriptor.Title} execution backend to {FormatModeLabel(nextMode)}",
        });
    }

    protected override View Render(ArtCraftStudioAppState state) => RenderStudioView(state.Status, state.ActiveDocument, state.OutputDocument, state.WebUrl, state.ExecutionMode);

    protected async Task ApplyExecutionAsync(string instructionOrCommand, string? explicitParamsJson, string targetDoc, string mode)
    {
        var result = await ExecuteStudioCommandAsync(instructionOrCommand, explicitParamsJson, targetDoc, mode);
        await Show(!result.IsValidCommand
            ? State with
            {
                ExecutionMode = result.ExecutionMode,
                LastCommandId = result.CommandId,
                LastCommandTitle = result.CommandTitle,
                LastCliInvocation = string.Empty,
                Status = result.Summary,
            }
            : State with
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

[Neuron("app.artcraft")]
public sealed class ArtCraftApp() : ArtCraftStudioAppBase("artcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("artcraft", typeof(ArtCraftApp));
}

[Neuron("app.vectorcraft")]
public sealed class VectorCraftApp() : ArtCraftStudioAppBase("vectorcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("vectorcraft", typeof(VectorCraftApp));
}

[Neuron("app.lightcraft")]
public sealed class LightCraftApp() : ArtCraftStudioAppBase("lightcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("lightcraft", typeof(LightCraftApp));
}

[Neuron("app.effectcraft")]
public sealed class EffectCraftApp() : ArtCraftStudioAppBase("effectcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("effectcraft", typeof(EffectCraftApp));
}

[Neuron("app.designcraft")]
public sealed class DesignCraftApp() : ArtCraftStudioAppBase("designcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("designcraft", typeof(DesignCraftApp));
}

[Neuron("app.pdfcraft")]
public sealed class PdfCraftApp() : ArtCraftStudioAppBase("pdfcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("pdfcraft", typeof(PdfCraftApp));
}

[Neuron("app.wordcraft")]
public sealed class WordCraftApp() : ArtCraftStudioAppBase("wordcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("wordcraft", typeof(WordCraftApp));
}

[Neuron("app.soundcraft")]
public sealed class SoundCraftApp() : ArtCraftStudioAppBase("soundcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("soundcraft", typeof(SoundCraftApp));
}

[Neuron("app.deckcraft")]
public sealed class DeckCraftApp() : ArtCraftStudioAppBase("deckcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("deckcraft", typeof(DeckCraftApp));
}

[Neuron("app.cadcraft")]
public sealed class CadCraftApp() : ArtCraftStudioAppBase("cadcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("cadcraft", typeof(CadCraftApp));
}

[Neuron("app.gridcraft")]
public sealed class GridCraftApp() : ArtCraftStudioAppBase("gridcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("gridcraft", typeof(GridCraftApp));
}

[Neuron("app.figcraft")]
public sealed class FigCraftApp() : ArtCraftStudioAppBase("figcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("figcraft", typeof(FigCraftApp));
}

[Neuron("app.texturecraft")]
public sealed class TextureCraftApp() : ArtCraftStudioAppBase("texturecraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("texturecraft", typeof(TextureCraftApp));
}

[Neuron("app.materialcraft")]
public sealed class MaterialCraftApp() : ArtCraftStudioAppBase("materialcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("materialcraft", typeof(MaterialCraftApp));
}

[Neuron("app.solidcraft")]
public sealed class SolidCraftApp() : ArtCraftStudioAppBase("solidcraft")
{
    public static AppBundle Bundle { get; } = CreateBundle("solidcraft", typeof(SolidCraftApp));
}
