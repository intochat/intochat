/*
# PhotoCraft
Role: Default
Shipped: true
Operations: open, edit, remove-background, run-command, switch-mode

Open-source Photoshop-compatible layered image editor (PSD, layers, alpha masks, adjustments) with dual execution:
1. Native Linux CLI mode (photocraft-cli / craft-cli run | serve) for fast headless/cloud execution without WASM overhead.
2. MCP Server mode (IArtCraftStudio + IMcpServer at {brain}/mcp/photocraft) for multi-step agentic tool workflows.
3. Optional WASM Web UI mode when interactive canvas editing is desired.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Modules.Apps;

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
        "native-linux",
        "/work/portrait.png",
        "/work/portrait.nobg.png",
        "layer.removeBackground",
        "Ready · Native Linux Engine (photocraft-cli)",
        "photocraft-cli run /work/portrait.png --cmd layer.removeBackground --params '{\"featherPx\":1,\"output\":\"layer-mask\"}' --out /work/portrait.nobg.png --format png",
        ArtCraftEngineRunner.CyanPreviewPngDataUrl,
        "PhotoCraft tools connected (Native Linux CLI + MCP).",
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
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "photocraft",
        "PhotoCraft",
        "1.1.0",
        [],
        new DigitalBrain.AppRequirements([]))
    {
        Accent = "#06B6D4",
        Category = "Studio",
    };

    internal static AppBundle HostedBundle { get; } = new(
        "photocraft",
        "1.1.0",
        new AppManifest(
            Title: "PhotoCraft",
            Description: "Photoshop-compatible image studio with Dual Execution: Native Linux CLI (photocraft-cli) for fast/cloud headless edits and MCP Server for agent tool workflows.",
            Operations:
            [
                new("open", "Connect PhotoCraft studio (Native Linux CLI + MCP) and open or inspect a document."),
                new("edit", "Edit an image from natural language (e.g. remove background, select subject, desaturate, gradient)."),
                new("remove-background", "Run AI background removal (layer.removeBackground) on the active or specified image."),
                new("run-command", "Execute a PhotoCraft engine command ID directly via Native Linux CLI or MCP."),
                new("switch-mode", "Switch execution backend between native-linux, mcp, and wasm-ui."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Toolbar:
            [
                new("open", Icon: "link", Label: "Connect editor"),
                new("remove-background", Icon: "wand-sparkles", Label: "Remove Background"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

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

    public View RenderSide() => new(
        "PhotoCraft Layers & Engine",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill("PhotoCraft Studio", "accent", "image"),
                        new TagPill(State.ExecutionMode, State.ExecutionMode == "native-linux" ? "ok" : "info", "cpu"),
                    ]) { Gap = 6 },
                    new Text(State.ActiveDocument) { Mono = true, Tabular = true },
                    new Text($"Output: {State.OutputDocument}") { Tone = "muted", Mono = true, Tabular = true },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "Execution Backend"),
            new Divider("EXECUTION MODE"),
            new Button(
                "Native Linux CLI (Fast / Cloud)",
                new ButtonAction("apps/photocraft", "switch-mode", ["native-linux"]),
                Tone: State.ExecutionMode == "native-linux" ? ButtonTone.Accent : ButtonTone.Neutral)
            {
                Small = true,
                Icon = "terminal",
            },
            new Button(
                "MCP Server (Agent Tools)",
                new ButtonAction("apps/photocraft", "switch-mode", ["mcp"]),
                Tone: State.ExecutionMode == "mcp" ? ButtonTone.Accent : ButtonTone.Neutral)
            {
                Small = true,
                Icon = "plug",
            },
            new Button(
                "WASM Web UI (Interactive)",
                new ButtonAction("apps/photocraft", "switch-mode", ["wasm-ui"]),
                Tone: State.ExecutionMode == "wasm-ui" ? ButtonTone.Accent : ButtonTone.Neutral)
            {
                Small = true,
                Icon = "globe",
            },
            new Divider("DOCUMENT LAYERS"),
            new Collection(State.Layers.Select((layer, idx) =>
                new Entry($"layer-{idx + 1}", layer, idx == 0 ? "Active" : "Visible")).ToArray()),
        ])
        { Gap = 8 });

    public View Render() => new(
        "PhotoCraft",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill("PhotoCraft 1.1", "accent", "image"),
                        new TagPill($"Mode: {FormatModeLabel(State.ExecutionMode)}", "ok", "cpu"),
                        new TagPill($"Cmd: {State.LastCommandId}", "neutral", "wand-sparkles"),
                        new TagPill(State.McpEndpointUri, "info", "network"),
                    ]) { Gap = 8 },
                    new Text(State.Status) { Tabular = true },
                    new Row(
                    [
                        new Button(
                            "Remove Background",
                            new ButtonAction("apps/photocraft", "remove-background", [State.ActiveDocument]),
                            Tone: ButtonTone.Accent)
                        {
                            Small = true,
                            Icon = "wand-sparkles",
                        },
                        new Button(
                            "Select Subject",
                            new ButtonAction("apps/photocraft", "run-command", ["select.subject"]))
                        {
                            Small = true,
                            Icon = "scan",
                        },
                        new Button(
                            "Desaturate",
                            new ButtonAction("apps/photocraft", "run-command", ["image.adjustments.desaturate"]))
                        {
                            Small = true,
                            Icon = "contrast",
                        },
                        new Button(
                            "512x512 Gradient",
                            new ButtonAction("apps/photocraft", "edit", ["make a 512x512 image with a blue-to-orange gradient"]))
                        {
                            Small = true,
                            Icon = "palette",
                        },
                    ]) { Gap = 8 },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "PhotoCraft · Native Linux CLI & MCP Image Studio"),
            new SplitPane(
                Leading: new Surface(
                    [
                        new Heading(State.LastCommandTitle, Level: 3, Subtitle: $"{State.ActiveDocument} -> {State.OutputDocument}"),
                        UI.WebBrowser(State.WebUrl ?? "http://localhost:18082", "PhotoCraft WASM Studio", "photocraft-wasm", 520, State.ExecutionMode),
                        new Text($"Preview Data URL: {State.PreviewDataUrl[..Math.Min(64, State.PreviewDataUrl.Length)]}...") { Mono = true, Tone = "muted" },
                        new Divider("NATIVE LINUX CLI / ENGINE INVOCATION"),
                        new Surface(
                            [
                                new Text(State.LastCliInvocation) { Mono = true, Tabular = true },
                            ],
                            Kind: SurfaceKind.Recessed),
                        new Row(
                        [
                            new TextField(
                                "edit-prompt",
                                string.Empty,
                                "Ask PhotoCraft (e.g. 'remove background from portrait.png', 'desaturate photo.jpg')...")
                            {
                                OnSubmit = new ButtonAction("apps/photocraft", "edit", ["remove background from portrait.png"]),
                            },
                            new Button(
                                "Run Edit",
                                new ButtonAction("apps/photocraft", "edit", ["remove background from portrait.png"]),
                                Tone: ButtonTone.Accent)
                            {
                                Icon = "play",
                            },
                        ]) { Gap = 8 },
                    ],
                    Kind: SurfaceKind.Elevated,
                    Title: "Canvas & Headless Engine Output",
                    Subtitle: $"Rendered via {FormatModeLabel(State.ExecutionMode)}"),
                Trailing: new Surface(
                    [
                        new RecordCardNode(
                            "Engine Execution Telemetry",
                            State.LastCommandId,
                            [
                                new RecordFieldNode("Execution Mode", FormatModeLabel(State.ExecutionMode)),
                                new RecordFieldNode("Input File", State.ActiveDocument),
                                new RecordFieldNode("Output File", State.OutputDocument),
                                new RecordFieldNode("MCP Neuron", State.McpEndpointUri),
                                new RecordFieldNode("WASM Endpoint", State.WebUrl ?? "disabled (headless)"),
                            ],
                            Subtitle: State.Status),
                        new Divider("EDIT HISTORY"),
                        new Collection(State.History.Select(h =>
                            new Entry(h.Id, $"{h.Title} [{h.ExecutionMode}]", $"{h.CommandId} -> {h.OutputDocument}")).ToArray()),
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Inspector & Pipeline History"),
                LeadingExtent: 480,
                Gap: 16),
        ])
        { Gap = 14 });

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
        var result = await ArtCraftEngineRunner.ExecutePhotoCraftAsync(
            instructionOrCommand,
            explicitParamsJson,
            targetDoc,
            mode,
            TryGetArtCraftStudio(),
            TryGetMcpServer,
            (err, reason) => Swallow(err, reason));

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
