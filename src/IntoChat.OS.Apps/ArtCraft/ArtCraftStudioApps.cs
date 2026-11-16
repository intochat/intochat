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
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

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
        "artcraft", "mcp", "/work/project.craft", "/work/project.export.png", "suite.inspect",
        "Inspect ArtCraft Workspace", string.Empty,
        "ArtCraft MCP server not connected. Connect ArtCraft in Settings to use tools.",
        ["ArtCraft Suite Hub · Disconnected"], null, "neuron://local/mcp/artcraft")
    { }

    [Invariant("ExecutionMode is wasm-ui, mcp, or native-linux."), JsonIgnore]
    public bool ValidExecutionMode => ExecutionMode is "wasm-ui" or "mcp" or "native-linux";

    [Invariant("AppId and ActiveDocument are non-empty."), JsonIgnore]
    public bool NonEmptyIdentity => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(ActiveDocument);

    public Rule SupportedExecutionMode(string? mode) => Decision.Rule(
        "invalid_execution_mode",
        mode,
        static m => string.IsNullOrWhiteSpace(m) || m.Trim().ToLowerInvariant() is not ("wasm-ui" or "mcp" or "native-linux"),
        "Execution mode must be wasm-ui, mcp, or native-linux.");

    public Decision<ArtCraftStudioAppState> SwitchExecutionMode([Sample("wasm-ui", "mcp", "native-linux", "unsupported")] string mode)
    {
        if (Decision.Check("switch-mode", SupportedExecutionMode(mode)) is { } refusal) { return refusal; }
        var normalized = mode.Trim().ToLowerInvariant();
        return Decision.Accept(this with { ExecutionMode = normalized, Status = $"Switched execution backend to {normalized}." });
    }

    [IgnoreExplore]
    public ArtCraftStudioAppState ApplyEngineOutcome(ArtCraftEngineResult result) => !result.IsValidCommand
        ? this with { ExecutionMode = result.ExecutionMode, LastCommandId = result.CommandId, LastCommandTitle = result.CommandTitle, LastCliInvocation = string.Empty, Status = result.Summary }
        : this with
        {
            ExecutionMode = result.ExecutionMode,
            ActiveDocument = result.InputDocument,
            OutputDocument = result.OutputDocument,
            LastCommandId = result.CommandId,
            LastCommandTitle = result.CommandTitle,
            LastCliInvocation = result.CliInvocation,
            Status = result.Summary,
            LayersOrTracks = result.LayersOrTracks,
        };

    public static ArtCraftStudioAppState For(string appId)
    {
        var d = ArtCraftCatalog.Get(appId);
        return new(
            d.Id, "mcp", d.DefaultDocument, d.DefaultOutputDocument, d.DefaultCommandId, $"Ready · {d.Title}",
            string.Empty, $"{d.Title} MCP server not connected.",
            d.DefaultLayersOrTracks, null, $"neuron://local/mcp/{d.Id}");
    }
}

internal static class ArtCraftConsoleLog
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Debug).AddConsole());
    internal static readonly ILogger Instance = Factory.CreateLogger("IntoChat.OS.Apps.ArtCraft");
}

public abstract class ArtCraftStudioAppCore<TState>(string appId, ILogger? logger = null) : AggregateScreen<TState>, IWithFilesVfs
    where TState : class, new()
{
    private readonly ILogger _appLogger = logger ?? ArtCraftConsoleLog.Instance;

    private ILogger AppLogger => _appLogger;

    public new TState State => base.State;
    public View Render() => PreviewView(State);
    public ArtCraftAppDescriptor Descriptor { get; } = ArtCraftCatalog.Get(appId);
    public IFilesVfsService? FilesVfs { get; set; }
    protected virtual string ActiveDocumentLabel => "Active";
    protected override string Title => Descriptor.Title;

    protected async Task<(string Mode, string Document, string? WebUrl, string McpUri, string Status)> ResolveOpenSessionCoreAsync(
        string? requestedDocument, string? requestedMode, string currentMode, string currentDocument, string? currentWebUrl, string currentMcpUri)
    {
        var mode = NormalizeModeOrCurrent(requestedMode, currentMode);
        var doc = NormalizeTextOrCurrent(requestedDocument, currentDocument);
        var webUrl = currentWebUrl;
        var mcpUri = currentMcpUri;

        if (FilesVfs is not null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(requestedDocument))
                {
                    if (await FilesVfs.ResolveFileAsync(requestedDocument.Trim()) is { } resolvedExplicit) { doc = resolvedExplicit.VfsPath; }
                }
                else if (await FilesVfs.ResolveFileAsync(doc) is { } resolvedCurrent) { doc = resolvedCurrent.VfsPath; }
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
                AppLogger.LogDebug(error, "Files VFS resolution is optional when opening {App}.", Descriptor.Title);
            }
        }

        var connected = false;
        if (TryGetOptional<IArtCraftStudio>("artcraft", $"ArtCraftStudioNeuron is optional when running {Descriptor.Title} in unit tests.") is { } studio)
        {
            try
            {
                var session = await studio.Open(Descriptor.Id);
                mcpUri = session.McpKey;
                webUrl = session.WebUrl ?? webUrl;
                connected = true;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                AppLogger.LogDebug(error, "ArtCraftStudioNeuron MCP endpoint is optional when opening {App} in local/headless mode.", Descriptor.Title);
            }
        }

        var status = connected
            ? $"{Descriptor.Title} tools connected ({FormatModeLabel(mode)}) · {ActiveDocumentLabel}: {doc}"
            : $"{Descriptor.Title} MCP server not connected ({FormatModeLabel(mode)}) · {ActiveDocumentLabel}: {doc}";
        return (mode, doc, webUrl, mcpUri, status);
    }

    protected async Task<ArtCraftEngineResult> ExecuteStudioCommandAsync(
        string instructionOrCommand, string? explicitParamsJson, string targetDoc, string mode)
    {
        ArtCraftEngineResult? result = null;
        if (FilesVfs is null && TryGetOptional<IArtCraftEngine>("apps/artcraft-engine", $"ArtCraftEngineNeuron is optional when running {Descriptor.Title} standalone.") is { } engine)
        {
            try { result = await engine.Execute(Descriptor.Id, instructionOrCommand, explicitParamsJson, targetDoc, mode); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                AppLogger.LogDebug(error, "ArtCraftEngineNeuron is optional when running {App} standalone.", Descriptor.Title);
            }
        }

        return result ?? await ArtCraftEngineRunner.ExecuteAsync(
            Descriptor.Id, instructionOrCommand, explicitParamsJson, targetDoc, mode,
            TryGetOptional<IArtCraftStudio>("artcraft", $"ArtCraftStudioNeuron is optional when running {Descriptor.Title} in unit tests."),
            mcpKey => TryGetOptional<IMcpServer>(mcpKey, $"McpServerNeuron is optional when running {Descriptor.Title} in unit tests."),
            (err, reason) => AppLogger.LogDebug(err, "{Reason}", reason),
            FilesVfs);
    }

    protected Node RenderStudioShell(
        string status, string activeDoc, string outputDoc, string? webUrl, string executionMode,
        string itemsTitle, IReadOnlyList<string> items, Node?[]? headerActions = null, Node?[]? inspectorExtras = null,
        Node? nativeCanvas = null, string? cliInvocation = null, string? mcpUri = null)
    {
        var pillKind = executionMode == "wasm-ui" ? StatusKind.Working : StatusKind.Done;
        var toolbar = HeaderBar(Descriptor.Title, status, [StatusPill(pillKind, FormatModeLabel(executionMode)), .. headerActions ?? []]);

        var sidebar = VStack(Space.Md)[
            SectionHeader("Document"),
            Card(activeDoc, $"{ActiveDocumentLabel} -> {outputDoc}")[
                VStack(Space.Xs, [.. inspectorExtras ?? []])
            ],
            items.Count > 0 ? SectionHeader(itemsTitle, $"{items.Count} item(s)") : null,
            items.Count > 0
                ? Surface(SurfaceKind.Recessed, padding: Space.Sm)[VStack(Space.Xs, [.. items.Select(item => Text(item).Tabular().ToNode())])].ToNode()
                : null
        ];

        var effectiveWebUrl = ResolveEmbeddedWebUrl(webUrl);
        Node body = executionMode == "wasm-ui"
            ? VStack(Space.Md)[
                Banner("Embedded WASM Studio", $"Hosted via IntoChat OS Kernel ({effectiveWebUrl}). Switch to MCP or CLI for instant headless execution."),
                WebBrowser(effectiveWebUrl, $"{Descriptor.Title} WASM Studio", $"{Descriptor.Id}-wasm", height: 480, executionMode: executionMode)
            ]
            : nativeCanvas ?? BuildDefaultNativeCanvas(activeDoc, outputDoc, executionMode, itemsTitle, items, cliInvocation, mcpUri);

        return AppShell($"{Descriptor.Id}-shell", sidebar: sidebar, body: body, toolbar: toolbar);
    }

    protected string ResolveEmbeddedWebUrl(string? candidateUrl)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl)) { return Descriptor.StaticWebPath; }
        var trimmed = candidateUrl.Trim();
        if (trimmed.StartsWith("http://localhost:180", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://127.0.0.1:180", StringComparison.OrdinalIgnoreCase))
        {
            return Descriptor.StaticWebPath;
        }
        return trimmed;
    }

    protected static bool IsLegacyUnhostedWasmState(string? executionMode, string? webUrl)
        => string.Equals(executionMode, "wasm-ui", StringComparison.OrdinalIgnoreCase)
           && (string.IsNullOrWhiteSpace(webUrl)
               || webUrl.StartsWith("http://localhost:180", StringComparison.OrdinalIgnoreCase)
               || webUrl.StartsWith("http://127.0.0.1:180", StringComparison.OrdinalIgnoreCase));

    private Node BuildDefaultNativeCanvas(
        string activeDoc, string outputDoc, string executionMode, string itemsTitle, IReadOnlyList<string> items, string? cliInvocation, string? mcpUri)
    {
        return VStack(Space.Md)[
            Grid(2, Space.Sm)[
                Metric("Active Document", activeDoc, Descriptor.Category, BadgeTone.Accent, Icons.FileText),
                Metric("Output Target", outputDoc, FormatModeLabel(executionMode), BadgeTone.Done, Icons.Sparkles)
            ],
            !string.IsNullOrWhiteSpace(cliInvocation)
                ? CodeBlock($"Execution Log · {FormatModeLabel(executionMode)}", cliInvocation, "bash", maxHeight: 160)
                : Surface(SurfaceKind.Elevated, "Studio Canvas", "Connect an ArtCraft MCP server in Settings to render previews and execute commands.")[
                    MutedText($"{Descriptor.Title} · {activeDoc} -> {outputDoc}")
                ],
            !string.IsNullOrWhiteSpace(mcpUri)
                ? MutedText($"MCP Endpoint: {mcpUri} · Supported formats: {string.Join(", ", Descriptor.SupportedExtensions)}").Tabular().ToNode()
                : null,
            items.Count > 0
                ? Table(itemsTitle, ["#", "Item / Track"], [.. items.Select((item, idx) => (IReadOnlyList<string>)[$"{idx + 1}", item])])
                : null
        ];
    }

    protected string FormatModeLabel(string mode) => mode switch
    {
        "mcp" => $"MCP Server ({Descriptor.Id}-mcp)",
        "wasm-ui" => "WASM Web UI",
        _ => $"Studio Runtime ({mode})",
    };

    protected static string NormalizeModeOrCurrent(string? requestedMode, string currentMode) =>
        string.IsNullOrWhiteSpace(requestedMode) ? currentMode : ArtCraftEngineRunner.NormalizeMode(requestedMode);

    protected static string NormalizeTextOrCurrent(string? requested, string fallback) =>
        string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();

    private T? TryGetOptional<T>(string key, string reason) where T : class
    {
        try { return Get<T>(key); }
        catch (Exception error) when (error is not OperationCanceledException) { AppLogger.LogDebug(error, "{Reason}", reason); return null; }
    }
}

public abstract class ArtCraftStudioAppBase(string appId, ILogger? logger = null) : ArtCraftStudioAppCore<ArtCraftStudioAppState>(appId, logger)
{
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        if (State.AppId != Descriptor.Id)
        {
            await Show(ArtCraftStudioAppState.For(Descriptor.Id));
            return;
        }

        if (IsLegacyUnhostedWasmState(State.ExecutionMode, State.WebUrl))
        {
            await Show(State with
            {
                ExecutionMode = "mcp",
                WebUrl = Descriptor.StaticWebPath,
                Status = $"{Descriptor.Title} studio ({FormatModeLabel("mcp")}) · Active: {State.ActiveDocument}",
            });
        }
    }

    protected static AppBundle CreateBundle(string appId, Type appType)
    {
        var d = ArtCraftCatalog.Get(appId);
        return new AppBundle(
            d.Id, "1.1.0",
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
                Settings: [], Slots: [], Icon: d.IconKey,
                Toolbar: [new("open", Icon: "link", Label: "Connect studio"), new("run-command", Icon: d.Icon, Label: d.DefaultCommandTitle)]),
            "", new Dictionary<string, string>(StringComparer.Ordinal))
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
        await Show(State with { AppId = Descriptor.Id, ExecutionMode = mode, ActiveDocument = doc, WebUrl = webUrl, McpEndpointUri = mcpUri, Status = status });
    }

    [On]
    public Task Edit(ArtCraftStudioAppEdit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyExecutionAsync(
            NormalizeTextOrCurrent(request.Prompt, Descriptor.DefaultCommandId), null,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument), NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task RunCommand(ArtCraftStudioAppRunCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyExecutionAsync(
            NormalizeTextOrCurrent(request.CommandId, Descriptor.DefaultCommandId), request.ParametersJson,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument), NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task SwitchMode(ArtCraftStudioAppSwitchMode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextMode = ArtCraftEngineRunner.NormalizeMode(request.Mode);
        return Show(State with { ExecutionMode = nextMode, Status = $"Switched {Descriptor.Title} execution backend to {FormatModeLabel(nextMode)}" });
    }

    public Task RunDefaultCommandAsync() => RunCommand(new ArtCraftStudioAppRunCommand(Descriptor.DefaultCommandId, Descriptor.DefaultParametersJson, State.ActiveDocument, State.ExecutionMode));

    protected override Node Render(ArtCraftStudioAppState state) => RenderStudioShell(
        state.Status, state.ActiveDocument, state.OutputDocument, state.WebUrl, state.ExecutionMode,
        "Layers & Channels", state.LayersOrTracks ?? [],
        headerActions:
        [
            (state.ExecutionMode == "mcp" ? Button("MCP") : GhostButton("MCP")).Key("mode-mcp").Small().OnClick<ArtCraftStudioAppState>(x => x.SwitchExecutionMode("mcp")),
            (state.ExecutionMode == "native-linux" ? Button("CLI") : GhostButton("CLI")).Key("mode-native-linux").Small().OnClick<ArtCraftStudioAppState>(x => x.SwitchExecutionMode("native-linux")),
            (state.ExecutionMode == "wasm-ui" ? Button("WASM") : GhostButton("WASM")).Key("mode-wasm-ui").Small().OnClick<ArtCraftStudioAppState>(x => x.SwitchExecutionMode("wasm-ui")),
            PrimaryButton(Descriptor.DefaultCommandTitle, Icons.Play).Key("run-default").Small().OnClick(() => RunDefaultCommandAsync()),
        ],
        inspectorExtras: [Text(state.LastCommandTitle).Muted()],
        cliInvocation: state.LastCliInvocation,
        mcpUri: state.McpEndpointUri);

    protected async Task ApplyExecutionAsync(string instructionOrCommand, string? explicitParamsJson, string targetDoc, string mode) =>
        await Show(State.ApplyEngineOutcome(await ExecuteStudioCommandAsync(instructionOrCommand, explicitParamsJson, targetDoc, mode)));
}

[Neuron("app.artcraft")]
public sealed class ArtCraftApp : ArtCraftStudioAppBase
{
    public ArtCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public ArtCraftApp(ILogger<ArtCraftApp>? logger = null) : base("artcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("artcraft", typeof(ArtCraftApp));
}

[Neuron("app.vectorcraft")]
public sealed class VectorCraftApp : ArtCraftStudioAppBase
{
    public VectorCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public VectorCraftApp(ILogger<VectorCraftApp>? logger = null) : base("vectorcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("vectorcraft", typeof(VectorCraftApp));
}

[Neuron("app.lightcraft")]
public sealed class LightCraftApp : ArtCraftStudioAppBase
{
    public LightCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public LightCraftApp(ILogger<LightCraftApp>? logger = null) : base("lightcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("lightcraft", typeof(LightCraftApp));
}

[Neuron("app.effectcraft")]
public sealed class EffectCraftApp : ArtCraftStudioAppBase
{
    public EffectCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public EffectCraftApp(ILogger<EffectCraftApp>? logger = null) : base("effectcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("effectcraft", typeof(EffectCraftApp));
}

[Neuron("app.designcraft")]
public sealed class DesignCraftApp : ArtCraftStudioAppBase
{
    public DesignCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public DesignCraftApp(ILogger<DesignCraftApp>? logger = null) : base("designcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("designcraft", typeof(DesignCraftApp));
}

[Neuron("app.pdfcraft")]
public sealed class PdfCraftApp : ArtCraftStudioAppBase
{
    public PdfCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public PdfCraftApp(ILogger<PdfCraftApp>? logger = null) : base("pdfcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("pdfcraft", typeof(PdfCraftApp));
}

[Neuron("app.wordcraft")]
public sealed class WordCraftApp : ArtCraftStudioAppBase
{
    public WordCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public WordCraftApp(ILogger<WordCraftApp>? logger = null) : base("wordcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("wordcraft", typeof(WordCraftApp));
}

[Neuron("app.soundcraft")]
public sealed class SoundCraftApp : ArtCraftStudioAppBase
{
    public SoundCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public SoundCraftApp(ILogger<SoundCraftApp>? logger = null) : base("soundcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("soundcraft", typeof(SoundCraftApp));
}

[Neuron("app.deckcraft")]
public sealed class DeckCraftApp : ArtCraftStudioAppBase
{
    public DeckCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public DeckCraftApp(ILogger<DeckCraftApp>? logger = null) : base("deckcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("deckcraft", typeof(DeckCraftApp));
}

[Neuron("app.cadcraft")]
public sealed class CadCraftApp : ArtCraftStudioAppBase
{
    public CadCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public CadCraftApp(ILogger<CadCraftApp>? logger = null) : base("cadcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("cadcraft", typeof(CadCraftApp));
}

[Neuron("app.gridcraft")]
public sealed class GridCraftApp : ArtCraftStudioAppBase
{
    public GridCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public GridCraftApp(ILogger<GridCraftApp>? logger = null) : base("gridcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("gridcraft", typeof(GridCraftApp));
}

[Neuron("app.figcraft")]
public sealed class FigCraftApp : ArtCraftStudioAppBase
{
    public FigCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public FigCraftApp(ILogger<FigCraftApp>? logger = null) : base("figcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("figcraft", typeof(FigCraftApp));
}

[Neuron("app.texturecraft")]
public sealed class TextureCraftApp : ArtCraftStudioAppBase
{
    public TextureCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public TextureCraftApp(ILogger<TextureCraftApp>? logger = null) : base("texturecraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("texturecraft", typeof(TextureCraftApp));
}

[Neuron("app.materialcraft")]
public sealed class MaterialCraftApp : ArtCraftStudioAppBase
{
    public MaterialCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public MaterialCraftApp(ILogger<MaterialCraftApp>? logger = null) : base("materialcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("materialcraft", typeof(MaterialCraftApp));
}

[Neuron("app.solidcraft")]
public sealed class SolidCraftApp : ArtCraftStudioAppBase
{
    public SolidCraftApp() : this(null) { }
    [ActivatorUtilitiesConstructor]
    public SolidCraftApp(ILogger<SolidCraftApp>? logger = null) : base("solidcraft", logger) { }
    public static AppBundle Bundle { get; } = CreateBundle("solidcraft", typeof(SolidCraftApp));
}
