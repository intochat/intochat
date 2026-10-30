/*
# FilmCraft
Role: Default
Shipped: true
Operations: open, edit, run-command, export, switch-mode

Open-source Premiere-compatible non-linear video editor (timeline tracks, razor split, ripple trim, Lumetri color, MP4 export)
with dual execution:
1. Native Linux CLI mode (filmcraft-cli / craft-cli exec | render | export) for fast headless/cloud timeline processing.
2. MCP Server mode (IArtCraftStudio + IMcpServer at {brain}/mcp/filmcraft) for agentic timeline tool calls.
3. Optional WASM Web UI mode for interactive browser timeline editing.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;
using UI = DigitalBrain.UI.Ui;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("filmcraft.open")]
public sealed record FilmCraftStudioOpen(
    string? Sequence = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("filmcraft.edit")]
public sealed record FilmCraftStudioEdit(
    string? Prompt = null,
    string? Sequence = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("filmcraft.run-command")]
public sealed record FilmCraftRunCommand(
    string? CommandId = null,
    string? ParametersJson = null,
    string? Sequence = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("filmcraft.export")]
public sealed record FilmCraftExport(
    string? Preset = null,
    string? Sequence = null,
    string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("filmcraft.switch-mode")]
public sealed record FilmCraftSwitchMode(
    string? Mode = null) : DigitalBrain.Signal;

public sealed record FilmCraftAppState(
    string ExecutionMode,
    string ActiveSequence,
    string OutputFile,
    string LastCommandId,
    string LastCommandTitle,
    string LastCliInvocation,
    string Status,
    string[] Tracks,
    string? WebUrl = "http://localhost:18083",
    string McpEndpointUri = "neuron://local/mcp/filmcraft")
{
    public FilmCraftAppState() : this(
        "native-linux",
        "/work/nordlys-launch.fcproj",
        "/work/nordlys-launch.cut.mp4",
        "timeline.razor",
        "Ready · Native Linux Timeline Engine (filmcraft-cli)",
        "filmcraft-cli exec /work/nordlys-launch.fcproj --cmd timeline.razor --params '{\"timecode\":\"00:00:03:12\",\"track\":\"V1\"}' --out /work/nordlys-launch.cut.mp4",
        "FilmCraft tools connected (Native Linux CLI + MCP).",
        [
            "V2 · Title Overlay (00:00:00:00 -> 00:00:05:00)",
            "V1 · A-Roll Main Camera (00:00:00:00 -> 00:00:14:00)",
            "A1 · Master Dialogue Bus (-14 LUFS)",
        ])
    {
    }
}

[DigitalBrain.App("filmcraft", Accent = "#8B5CF6", Category = "Studio")]
public sealed class FilmCraftApp : DigitalBrain.App<FilmCraftAppState>
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "filmcraft",
        "FilmCraft",
        "1.1.0",
        [],
        new DigitalBrain.AppRequirements([]))
    {
        Accent = "#8B5CF6",
        Category = "Studio",
    };

    internal static AppBundle HostedBundle { get; } = new(
        "filmcraft",
        "1.1.0",
        new AppManifest(
            Title: "FilmCraft",
            Description: "Premiere-compatible non-linear video editor (VideoCraft / FilmCraft) with Dual Execution: Native Linux CLI (filmcraft-cli) and MCP Server.",
            Operations:
            [
                new("open", "Connect FilmCraft studio (Native Linux CLI + MCP) and load a sequence."),
                new("edit", "Edit a video sequence from natural language (razor split, ripple trim, Lumetri color, export)."),
                new("run-command", "Execute a FilmCraft timeline command ID via Native Linux CLI or MCP."),
                new("export", "Render and export the active video sequence to H.264 MP4."),
                new("switch-mode", "Switch execution backend between native-linux, mcp, and wasm-ui."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Toolbar:
            [
                new("open", Icon: "link", Label: "Connect editor"),
                new("export", Icon: "film", Label: "Export MP4"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    [DigitalBrain.On]
    public async Task Open(FilmCraftStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var seq = string.IsNullOrWhiteSpace(request.Sequence)
            ? State.ActiveSequence
            : request.Sequence.Trim();
        var webUrl = State.WebUrl;
        var mcpUri = State.McpEndpointUri;

        if (TryGetArtCraftStudio() is { } studio)
        {
            try
            {
                var session = await studio.Open("filmcraft");
                mcpUri = session.McpKey;
                webUrl = session.WebUrl ?? webUrl;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "ArtCraftStudioNeuron MCP endpoint is optional when opening FilmCraft in local/headless mode.");
            }
        }

        Set(State with
        {
            ExecutionMode = mode,
            ActiveSequence = seq,
            WebUrl = webUrl,
            McpEndpointUri = mcpUri,
            Status = $"FilmCraft tools connected ({FormatModeLabel(mode)}) · Sequence: {seq}",
        });
    }

    [DigitalBrain.On]
    public async Task Edit(FilmCraftStudioEdit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = string.IsNullOrWhiteSpace(request.Prompt)
            ? "razor split timeline at 00:00:03:12"
            : request.Prompt.Trim();
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var seq = string.IsNullOrWhiteSpace(request.Sequence)
            ? State.ActiveSequence
            : request.Sequence.Trim();

        await ApplyTimelineExecutionAsync(prompt, explicitParamsJson: null, seq, mode);
    }

    [DigitalBrain.On]
    public async Task RunCommand(FilmCraftRunCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cmdId = string.IsNullOrWhiteSpace(request.CommandId)
            ? "timeline.razor"
            : request.CommandId.Trim();
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var seq = string.IsNullOrWhiteSpace(request.Sequence)
            ? State.ActiveSequence
            : request.Sequence.Trim();

        await ApplyTimelineExecutionAsync(cmdId, request.ParametersJson, seq, mode);
    }

    [DigitalBrain.On]
    public async Task Export(FilmCraftExport request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = string.IsNullOrWhiteSpace(request.Preset) ? "h264-1080p60" : request.Preset.Trim();
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var seq = string.IsNullOrWhiteSpace(request.Sequence)
            ? State.ActiveSequence
            : request.Sequence.Trim();

        await ApplyTimelineExecutionAsync(
            "sequence.exportMp4",
            explicitParamsJson: null,
            seq,
            mode);
    }

    [DigitalBrain.On]
    public void SwitchMode(FilmCraftSwitchMode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextMode = ArtCraftEngineRunner.NormalizeMode(request.Mode);
        Set(State with
        {
            ExecutionMode = nextMode,
            Status = $"Switched FilmCraft execution backend to {FormatModeLabel(nextMode)}",
        });
    }

    public View RenderSide() => new(
        "Sequence Tracks & Engine",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill("FilmCraft Studio", "accent", "film"),
                        new TagPill(State.ExecutionMode, State.ExecutionMode == "native-linux" ? "ok" : "info", "cpu"),
                    ]) { Gap = 6 },
                    new Text(State.ActiveSequence) { Mono = true, Tabular = true },
                    new Text($"Out: {State.OutputFile}") { Tone = "muted", Mono = true, Tabular = true },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "VideoCraft / FilmCraft Engine"),
            new Divider("EXECUTION MODE"),
            new Button(
                "Native Linux CLI (Fast / Cloud)",
                new ButtonAction("apps/filmcraft", "switch-mode", ["native-linux"]),
                Tone: State.ExecutionMode == "native-linux" ? ButtonTone.Accent : ButtonTone.Neutral)
            {
                Small = true,
                Icon = "terminal",
            },
            new Button(
                "MCP Server (Agent Tools)",
                new ButtonAction("apps/filmcraft", "switch-mode", ["mcp"]),
                Tone: State.ExecutionMode == "mcp" ? ButtonTone.Accent : ButtonTone.Neutral)
            {
                Small = true,
                Icon = "plug",
            },
            new Button(
                "WASM Web UI (Interactive)",
                new ButtonAction("apps/filmcraft", "switch-mode", ["wasm-ui"]),
                Tone: State.ExecutionMode == "wasm-ui" ? ButtonTone.Accent : ButtonTone.Neutral)
            {
                Small = true,
                Icon = "globe",
            },
            new Divider("TIMELINE TRACKS"),
            new Collection(State.Tracks.Select((track, idx) =>
                new Entry($"track-{idx + 1}", track, "Armed")).ToArray()),
        ])
        { Gap = 8 });

    public View Render() => new(
        "FilmCraft",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill("FilmCraft 1.1 (VideoCraft)", "accent", "film"),
                        new TagPill($"Mode: {FormatModeLabel(State.ExecutionMode)}", "ok", "cpu"),
                        new TagPill($"Cmd: {State.LastCommandId}", "neutral", "scissors"),
                        new TagPill(State.McpEndpointUri, "info", "network"),
                    ]) { Gap = 8 },
                    new Text(State.Status) { Tabular = true },
                    new Row(
                    [
                        new Button(
                            "Razor Split (00:03:12)",
                            new ButtonAction("apps/filmcraft", "run-command", ["timeline.razor"]),
                            Tone: ButtonTone.Accent)
                        {
                            Small = true,
                            Icon = "scissors",
                        },
                        new Button(
                            "Ripple Trim (-24f)",
                            new ButtonAction("apps/filmcraft", "run-command", ["timeline.rippleTrim"]))
                        {
                            Small = true,
                            Icon = "sliders-horizontal",
                        },
                        new Button(
                            "Lumetri Auto Grade",
                            new ButtonAction("apps/filmcraft", "run-command", ["color.lumetri.auto"]))
                        {
                            Small = true,
                            Icon = "palette",
                        },
                        new Button(
                            "Export 1080p60 MP4",
                            new ButtonAction("apps/filmcraft", "export", ["h264-1080p60"]))
                        {
                            Small = true,
                            Icon = "download",
                        },
                    ]) { Gap = 8 },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "FilmCraft (VideoCraft) · Native Linux CLI & MCP Non-Linear Video Studio"),
            new SplitPane(
                Leading: new Surface(
                    [
                        new Heading(State.LastCommandTitle, Level: 3, Subtitle: $"{State.ActiveSequence} -> {State.OutputFile}"),
                        UI.WebBrowser(State.WebUrl ?? "http://localhost:18083", "FilmCraft WASM Studio", "filmcraft-wasm", 520, State.ExecutionMode),
                        new Divider("NATIVE LINUX CLI / TIMELINE ENGINE INVOCATION"),
                        new Surface(
                            [
                                new Text(State.LastCliInvocation) { Mono = true, Tabular = true },
                            ],
                            Kind: SurfaceKind.Recessed),
                        new Collection(State.Tracks.Select((track, idx) =>
                            new Entry($"tl-{idx + 1}", track, "Timeline Track")).ToArray()),
                    ],
                    Kind: SurfaceKind.Elevated,
                    Title: "Program Monitor & Non-Linear Timeline",
                    Subtitle: $"Driven via {FormatModeLabel(State.ExecutionMode)}"),
                Trailing: new Surface(
                    [
                        new RecordCardNode(
                            "Timeline Engine Telemetry",
                            State.LastCommandId,
                            [
                                new RecordFieldNode("Execution Mode", FormatModeLabel(State.ExecutionMode)),
                                new RecordFieldNode("Sequence", State.ActiveSequence),
                                new RecordFieldNode("Rendered Output", State.OutputFile),
                                new RecordFieldNode("MCP Neuron", State.McpEndpointUri),
                                new RecordFieldNode("WASM Endpoint", State.WebUrl ?? "disabled (headless)"),
                            ],
                            Subtitle: State.Status),
                    ],
                    Kind: SurfaceKind.Acrylic,
                    Title: "Sequence Inspector"),
                LeadingExtent: 480,
                Gap: 16),
        ])
        { Gap = 14 });

    [DigitalBrain.Scenario("FilmCraft executes timeline razor split in Native Linux CLI mode and exports MP4 in MCP mode")]
    public async Task VerifyFilmCraftDualModeAndTimelineScenario()
    {
        await Open(new FilmCraftStudioOpen("/work/demo-reel.fcproj", "native-linux"));
        await Edit(new FilmCraftStudioEdit("razor split timeline at 00:00:03:12", "/work/demo-reel.fcproj", "native-linux"));
        if (State.LastCommandId != "timeline.razor" || State.ExecutionMode != "native-linux")
        {
            throw new InvalidOperationException("Expected FilmCraftApp to execute timeline.razor in native-linux mode.");
        }

        SwitchMode(new FilmCraftSwitchMode("mcp"));
        await Export(new FilmCraftExport("h264-1080p60", "/work/demo-reel.fcproj", "mcp"));
        if (State.ExecutionMode != "mcp" || State.LastCommandId != "sequence.exportMp4")
        {
            throw new InvalidOperationException("Expected FilmCraftApp to switch to MCP mode and export H.264 MP4.");
        }
    }

    private async Task ApplyTimelineExecutionAsync(
        string instructionOrCommand,
        string? explicitParamsJson,
        string sequence,
        string mode)
    {
        var result = await ArtCraftEngineRunner.ExecuteFilmCraftAsync(
            instructionOrCommand,
            explicitParamsJson,
            sequence,
            mode,
            TryGetArtCraftStudio(),
            TryGetMcpServer,
            (err, reason) => Swallow(err, reason));

        Set(State with
        {
            ExecutionMode = result.ExecutionMode,
            ActiveSequence = result.InputDocument,
            OutputFile = result.OutputDocument,
            LastCommandId = result.CommandId,
            LastCommandTitle = result.CommandTitle,
            LastCliInvocation = result.CliInvocation,
            Status = result.Summary,
            Tracks = result.LayersOrTracks,
        });
    }

    private static string FormatModeLabel(string mode) => mode switch
    {
        "mcp" => "MCP Server (filmcraft-mcp)",
        "wasm-ui" => "WASM Web UI",
        _ => "Native Linux CLI (filmcraft-cli)",
    };

    private IArtCraftStudio? TryGetArtCraftStudio()
    {
        try
        {
            return Get<IArtCraftStudio>("artcraft");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "ArtCraftStudioNeuron is optional when running FilmCraftApp in unit tests.");
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
            Swallow(error, "McpServerNeuron is optional when running FilmCraftApp in unit tests.");
            return null;
        }
    }
}
