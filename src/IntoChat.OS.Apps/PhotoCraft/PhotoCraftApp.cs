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
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Apps.PhotoCraft;

[DigitalBrain.Signal("photocraft.open")]
public sealed record PhotoCraftStudioOpen(
    string? Document = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("photocraft.edit")]
public sealed record PhotoCraftStudioEdit(
    string? Prompt = null,
    string? Document = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("photocraft.remove-background")]
public sealed record PhotoCraftRemoveBackground(
    string? Document = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("photocraft.run-command")]
public sealed record PhotoCraftRunCommand(
    string? CommandId = null,
    string? ParametersJson = null,
    string? Document = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("photocraft.switch-mode")]
public sealed record PhotoCraftSwitchMode(
    string? Mode = null) : DigitalBrain.Signal;

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

[DigitalBrain.App("photocraft", Accent = "#06B6D4", Category = "Studio")]
public sealed class PhotoCraftApp : DigitalBrain.App<PhotoCraftAppState>
{
    public IFilesVfsService? FilesVfs { get; set; }

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(PhotoCraftApp));

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

    [DigitalBrain.On]
    public async Task Open(PhotoCraftStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var doc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : request.Document.Trim();
        var webUrl = State.WebUrl;
        var mcpUri = State.McpEndpointUri;

        if (FilesVfs is not null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(request.Document))
                {
                    if (await FilesVfs.ResolveFileAsync(request.Document.Trim()) is { } resolvedExplicit)
                    {
                        doc = resolvedExplicit.VfsPath;
                    }
                }
                else if (await FilesVfs.ResolveFileAsync(doc) is { } resolvedCurrent)
                {
                    doc = resolvedCurrent.VfsPath;
                }
                else if (string.Equals(doc, "/work/portrait.png", StringComparison.OrdinalIgnoreCase))
                {
                    var files = await FilesVfs.ListFilesAsync();
                    if (files.FirstOrDefault(f => ArtCraftEngineRunner.IsImageExtension(f.Name)) is { } firstImage)
                    {
                        doc = firstImage.VfsPath;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "Files VFS resolution is optional when opening PhotoCraft.");
            }
        }

        if (TryGetArtCraftStudio() is { } studio)
        {
            try
            {
                var session = await studio.Open("photocraft");
                mcpUri = session.McpKey;
                webUrl = session.WebUrl ?? webUrl;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "ArtCraftStudioNeuron MCP endpoint is optional when opening PhotoCraft in local/headless mode.");
            }
        }

        Set(State with
        {
            ExecutionMode = mode,
            ActiveDocument = doc,
            WebUrl = webUrl,
            McpEndpointUri = mcpUri,
            Status = $"PhotoCraft tools connected ({FormatModeLabel(mode)}) · Active: {doc}",
        });
    }

    [DigitalBrain.On]
    public async Task Edit(PhotoCraftStudioEdit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = string.IsNullOrWhiteSpace(request.Prompt)
            ? "remove background from portrait.png"
            : request.Prompt.Trim();
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var targetDoc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : request.Document.Trim();

        await ApplyEngineExecutionAsync(prompt, explicitParamsJson: null, targetDoc, mode);
    }

    [DigitalBrain.On]
    public async Task RemoveBackground(PhotoCraftRemoveBackground request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var targetDoc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : request.Document.Trim();

        await ApplyEngineExecutionAsync($"remove background from {targetDoc}", explicitParamsJson: null, targetDoc, mode);
    }

    [DigitalBrain.On]
    public async Task RunCommand(PhotoCraftRunCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cmdId = string.IsNullOrWhiteSpace(request.CommandId)
            ? "layer.removeBackground"
            : request.CommandId.Trim();
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var targetDoc = string.IsNullOrWhiteSpace(request.Document)
            ? State.ActiveDocument
            : request.Document.Trim();

        await ApplyEngineExecutionAsync(cmdId, request.ParametersJson, targetDoc, mode);
    }

    [DigitalBrain.On]
    public void SwitchMode(PhotoCraftSwitchMode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextMode = ArtCraftEngineRunner.NormalizeMode(request.Mode);
        Set(State with
        {
            ExecutionMode = nextMode,
            Status = $"Switched PhotoCraft execution backend to {FormatModeLabel(nextMode)}",
        });
    }

    public View? RenderSide() => null;

    public View Render() => new(
        "PhotoCraft",
        new Column(
        [
            new Text($"{State.Status} · {State.ActiveDocument} -> {State.OutputDocument}") { Tone = "muted", Tabular = true },
            UI.WebBrowser(State.WebUrl ?? "http://localhost:18082", "PhotoCraft WASM Studio", "photocraft-wasm", 0, State.ExecutionMode),
        ])
        {
            Gap = 4,
            Extents = [24, 0],
        });

    [DigitalBrain.Scenario("PhotoCraft executes background removal in Native Linux CLI mode and switches to MCP mode")]
    public async Task VerifyPhotoCraftDualModeAndBackgroundRemovalScenario()
    {
        await Open(new PhotoCraftStudioOpen("/work/product-shoe.png", "native-linux"));
        await RemoveBackground(new PhotoCraftRemoveBackground("/work/product-shoe.png", "native-linux"));
        if (State.LastCommandId != "layer.removeBackground"
            || State.ExecutionMode != "native-linux"
            || !State.OutputDocument.EndsWith(".nobg.png", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected PhotoCraftApp to run layer.removeBackground in native-linux mode.");
        }

        SwitchMode(new PhotoCraftSwitchMode("mcp"));
        await Edit(new PhotoCraftStudioEdit("make a 512x512 image with a blue-to-orange gradient", "/work/canvas.psd", "mcp"));
        if (State.ExecutionMode != "mcp" || State.LastCommandId != "layer.newFillLayer.gradient")
        {
            throw new InvalidOperationException("Expected PhotoCraftApp to switch to MCP mode and execute layer.newFillLayer.gradient.");
        }
    }

    private async Task ApplyEngineExecutionAsync(
        string instructionOrCommand,
        string? explicitParamsJson,
        string targetDoc,
        string mode)
    {
        ArtCraftExecutionResult result;
        if (FilesVfs is null && TryGetArtCraftEngine() is { } engine)
        {
            try
            {
                var engineRes = await engine.ExecutePhotoCraft(instructionOrCommand, explicitParamsJson, targetDoc, mode);
                result = new ArtCraftExecutionResult(
                    engineRes.App,
                    engineRes.ExecutionMode,
                    engineRes.CommandId,
                    engineRes.CommandTitle,
                    engineRes.ParametersJson,
                    engineRes.InputDocument,
                    engineRes.OutputDocument,
                    engineRes.CliInvocation,
                    engineRes.Summary,
                    engineRes.PreviewDataUrl,
                    engineRes.LayersOrTracks,
                    engineRes.IsValidCommand,
                    engineRes.OutputNeuronUri);
            }
            catch (Exception error) when (error is not OperationCanceledException and not UnauthorizedAccessException)
            {
                Swallow(error, "ArtCraftEngineNeuron is optional when running PhotoCraftApp standalone.");
                result = await ArtCraftEngineRunner.ExecutePhotoCraftAsync(
                    instructionOrCommand,
                    explicitParamsJson,
                    targetDoc,
                    mode,
                    TryGetArtCraftStudio(),
                    TryGetMcpServer,
                    (err, reason) => Swallow(err, reason),
                    FilesVfs);
            }
        }
        else
        {
            result = await ArtCraftEngineRunner.ExecutePhotoCraftAsync(
                instructionOrCommand,
                explicitParamsJson,
                targetDoc,
                mode,
                TryGetArtCraftStudio(),
                TryGetMcpServer,
                (err, reason) => Swallow(err, reason),
                FilesVfs);
        }

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

        var entry = new PhotoCraftHistoryEntry(
            $"edit-{State.History.Length + 1}",
            result.CommandId,
            result.CommandTitle,
            result.ExecutionMode,
            result.OutputDocument,
            result.CliInvocation);

        Set(State with
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

    private static string FormatModeLabel(string mode) => mode switch
    {
        "mcp" => "MCP Server (photocraft-mcp)",
        "wasm-ui" => "WASM Web UI",
        _ => "Native Linux CLI (photocraft-cli)",
    };

    private IArtCraftEngine? TryGetArtCraftEngine()
    {
        try
        {
            return Get<IArtCraftEngine>("apps/artcraft-engine");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "ArtCraftEngineNeuron is optional when running PhotoCraftApp standalone.");
            return null;
        }
    }

    private IArtCraftStudio? TryGetArtCraftStudio()
    {
        try
        {
            return Get<IArtCraftStudio>("artcraft");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "ArtCraftStudioNeuron is optional when running PhotoCraftApp in unit tests.");
            return null;
        }
    }

    private IMcpServer? TryGetMcpServer(string mcpKey)
    {
        try
        {
            return Get<IMcpServer>(mcpKey);
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "McpServerNeuron is optional when running PhotoCraftApp in unit tests.");
            return null;
        }
    }
}
