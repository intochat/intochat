/*
# PhotoCraft
Role: Default
Shipped: true
Operations: open, edit, remove-background, run-command, switch-mode

Open-source Photoshop-compatible layered image editor (PSD, layers, alpha masks, adjustments) with dual execution:
1. Native Linux CLI mode (photocraft-cli / craft-cli run | serve) for fast headless/cloud execution without WASM overhead.
2. MCP Server mode (IArtCraftStudio + IMcpServer at {brain}/mcp/photocraft) for multi-step agentic tool workflows.
3. Interactive WASM Web UI mode for full-card canvas editing.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Apps.ArtCraft;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.PhotoCraft;

[Signal("photocraft.open")]
public sealed record PhotoCraftStudioOpen(string? Document = null, string? Mode = null) : Signal;

[Signal("photocraft.edit")]
public sealed record PhotoCraftStudioEdit(string? Prompt = null, string? Document = null, string? Mode = null) : Signal;

[Signal("photocraft.remove-background")]
public sealed record PhotoCraftRemoveBackground(string? Document = null, string? Mode = null) : Signal;

[Signal("photocraft.run-command")]
public sealed record PhotoCraftRunCommand(string? CommandId = null, string? ParametersJson = null, string? Document = null, string? Mode = null) : Signal;

[Signal("photocraft.switch-mode")]
public sealed record PhotoCraftSwitchMode(string? Mode = null) : Signal;

public sealed record PhotoCraftHistoryEntry(
    string Id,
    string CommandId,
    string Title,
    string ExecutionMode,
    string OutputDocument,
    string CliInvocation);

public sealed record PhotoCraftAppState(
    string ExecutionMode,
    string ActiveDocument,
    string OutputDocument,
    string LastCommandId,
    string LastCommandTitle,
    string LastCliInvocation,
    string PreviewDataUrl,
    string Status,
    string[] Layers,
    PhotoCraftHistoryEntry[] History,
    string? WebUrl = "http://localhost:18082",
    string McpEndpointUri = "neuron://local/mcp/photocraft")
{
    public PhotoCraftAppState() : this(
        "wasm-ui",
        "/work/portrait.png",
        "/work/portrait.nobg.png",
        "layer.removeBackground",
        "Ready · PhotoCraft Studio",
        "photocraft-cli run /work/portrait.png --cmd layer.removeBackground --params '{\"featherPx\":1,\"output\":\"layer-mask\"}' --out /work/portrait.nobg.png --format png",
        ArtCraftEngineRunner.CyanPreviewPngDataUrl,
        "PhotoCraft tools connected.",
        [
            "Subject Layer (100% · Normal)",
            "Background Layer (Locked · 100%)",
        ],
        [])
    {
    }
}

[Neuron("app.photocraft")]
public sealed class PhotoCraftApp() : ArtCraftStudioAppCore<PhotoCraftAppState>("photocraft")
{
    public const string AppName = ShippedApps.PhotoCraft;

    public static AppBundle Bundle { get; } = new(
        "photocraft",
        "1.1.0",
        new AppManifest(
            Title: "PhotoCraft",
            Description: "Photoshop-compatible layered image studio for editing images, removing backgrounds, adjusting levels, and generating graphics.",
            Operations:
            [
                new("open", "Connect PhotoCraft studio and open or inspect a document."),
                new("edit", "Edit an image from natural language (e.g. remove background, select subject, desaturate, gradient)."),
                new("remove-background", "Run AI background removal (layer.removeBackground) on the active or specified image."),
                new("run-command", "Execute a PhotoCraft engine command ID directly via Native Linux CLI or MCP."),
                new("switch-mode", "Switch execution backend between native-linux, mcp, and wasm-ui."),
            ],
            Settings: [],
            Slots: [],
            Icon: "image",
            Dock: 5, Studio: true,
            Toolbar:
            [
                new("open", Icon: "link", Label: "Connect editor"),
                new("remove-background", Icon: "wand-sparkles", Label: "Remove Background"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(PhotoCraftApp).AssemblyQualifiedName,
    };

    [On]
    public async Task Open(PhotoCraftStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (mode, doc, webUrl, mcpUri, status) = await ResolveOpenSessionCoreAsync(
            request.Document, request.Mode, State.ExecutionMode, State.ActiveDocument, State.WebUrl, State.McpEndpointUri);
        await Show(State with { ExecutionMode = mode, ActiveDocument = doc, WebUrl = webUrl, McpEndpointUri = mcpUri, Status = status });
    }

    [On]
    public Task Edit(PhotoCraftStudioEdit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyEngineExecutionAsync(
            NormalizeTextOrCurrent(request.Prompt, "remove background from portrait.png"),
            explicitParamsJson: null,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument),
            NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task RemoveBackground(PhotoCraftRemoveBackground request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetDoc = NormalizeTextOrCurrent(request.Document, State.ActiveDocument);
        return ApplyEngineExecutionAsync(
            $"remove background from {targetDoc}",
            explicitParamsJson: null,
            targetDoc,
            NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task RunCommand(PhotoCraftRunCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyEngineExecutionAsync(
            NormalizeTextOrCurrent(request.CommandId, "layer.removeBackground"),
            request.ParametersJson,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument),
            NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task SwitchMode(PhotoCraftSwitchMode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextMode = ArtCraftEngineRunner.NormalizeMode(request.Mode);
        return Show(State with { ExecutionMode = nextMode, Status = $"Switched PhotoCraft execution backend to {FormatModeLabel(nextMode)}" });
    }

    protected override View Render(PhotoCraftAppState state) => RenderStudioView(state.Status, state.ActiveDocument, state.OutputDocument, state.WebUrl ?? "http://localhost:18082", state.ExecutionMode);

    private async Task ApplyEngineExecutionAsync(string instructionOrCommand, string? explicitParamsJson, string targetDoc, string mode)
    {
        var result = await ExecuteStudioCommandAsync(instructionOrCommand, explicitParamsJson, targetDoc, mode);
        if (!result.IsValidCommand)
        {
            await Show(State with
            {
                ExecutionMode = result.ExecutionMode,
                LastCommandId = result.CommandId,
                LastCommandTitle = result.CommandTitle,
                LastCliInvocation = string.Empty,
                Status = result.Summary,
            });
            return;
        }

        var entry = new PhotoCraftHistoryEntry(
            $"edit-{State.History.Length + 1}",
            result.CommandId,
            result.CommandTitle,
            result.ExecutionMode,
            result.OutputDocument,
            result.CliInvocation);

        await Show(State with
        {
            ExecutionMode = result.ExecutionMode,
            ActiveDocument = result.InputDocument,
            OutputDocument = result.OutputDocument,
            LastCommandId = result.CommandId,
            LastCommandTitle = result.CommandTitle,
            LastCliInvocation = result.CliInvocation,
            PreviewDataUrl = result.PreviewDataUrl,
            Status = result.Summary,
            Layers = result.LayersOrTracks,
            History = [.. State.History.TakeLast(9), entry],
        });
    }
}
