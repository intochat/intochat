/*
# FilmCraft
Role: Default
Shipped: true
Operations: open, edit, run-command, export, switch-mode

Open-source Premiere-compatible non-linear video editor (timeline tracks, razor split, ripple trim, Lumetri color, MP4 export)
with dual execution:
1. Native Linux CLI mode (filmcraft-cli / craft-cli exec | render | export) for fast headless/cloud timeline processing.
2. MCP Server mode (IArtCraftStudio + IMcpServer at {brain}/mcp/filmcraft) for agentic timeline tool calls.
3. Interactive WASM Web UI mode for full-card browser timeline editing.
*/
using DigitalBrain.UI;
using IntoChat.OS.Apps.ArtCraft;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.FilmCraft;

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
        "wasm-ui",
        "/work/launch-trailer.fcproj",
        "/work/launch-trailer.cut.mp4",
        "timeline.razor",
        "Ready · FilmCraft Studio",
        "filmcraft-cli exec /work/launch-trailer.fcproj --cmd timeline.razor --params '{\"timecode\":\"00:00:03:12\",\"track\":\"V1\"}' --out /work/launch-trailer.cut.mp4",
        "FilmCraft tools connected.",
        [
            "V2 · Title Overlay (00:00:00:00 -> 00:00:05:00)",
            "V1 · A-Roll Main Camera (00:00:00:00 -> 00:00:14:00)",
            "A1 · Master Dialogue Bus (-14 LUFS)",
        ])
    {
    }
}

[DigitalBrain.App("filmcraft", Accent = "#8B5CF6", Category = "Studio")]
public sealed class FilmCraftApp() : ArtCraftStudioAppCore<FilmCraftAppState>("filmcraft")
{
    protected override string ActiveDocumentLabel => "Sequence";

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(FilmCraftApp));

    public const string AppName = ShippedApps.FilmCraft;

    public static AppBundle Bundle { get; } = new(
        "filmcraft",
        "1.1.0",
        new AppManifest(
            Title: "FilmCraft",
            Description: "Premiere-compatible non-linear video editor (VideoCraft / FilmCraft) with full-card WASM Web UI, Native Linux CLI (filmcraft-cli), and MCP Server.",
            Operations:
            [
                new("open", "Connect FilmCraft studio and load a sequence."),
                new("edit", "Edit a video sequence from natural language (razor split, ripple trim, Lumetri color, export)."),
                new("run-command", "Execute a FilmCraft timeline command ID via Native Linux CLI or MCP."),
                new("export", "Render and export the active video sequence to H.264 MP4."),
                new("switch-mode", "Switch execution backend between native-linux, mcp, and wasm-ui."),
            ],
            Settings: [],
            Slots: [],
            Icon: "film",
            Dock: 6, Studio: true,
            Toolbar:
            [
                new("open", Icon: "link", Label: "Connect editor"),
                new("export", Icon: "film", Label: "Export MP4"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(FilmCraftApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public async Task Open(FilmCraftStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (mode, seq, webUrl, mcpUri, status) = await ResolveOpenSessionCoreAsync(
            request.Sequence,
            request.Mode,
            State.ExecutionMode,
            State.ActiveSequence,
            State.WebUrl,
            State.McpEndpointUri);
        Set(State with
        {
            ExecutionMode = mode,
            ActiveSequence = seq,
            WebUrl = webUrl,
            McpEndpointUri = mcpUri,
            Status = status,
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
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? State.ExecutionMode
            : ArtCraftEngineRunner.NormalizeMode(request.Mode);
        var seq = string.IsNullOrWhiteSpace(request.Sequence)
            ? State.ActiveSequence
            : request.Sequence.Trim();

        await ApplyTimelineExecutionAsync("sequence.exportMp4", explicitParamsJson: null, seq, mode);
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

    public View? RenderSide() => null;

    public View Render() => RenderStudioView(State.Status, State.ActiveSequence, State.OutputFile, State.WebUrl ?? "http://localhost:18083", State.ExecutionMode);

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
        var result = await ExecuteStudioCommandAsync(instructionOrCommand, explicitParamsJson, sequence, mode);
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
            ActiveSequence = result.InputDocument,
            OutputFile = result.OutputDocument,
            LastCommandId = result.CommandId,
            LastCommandTitle = result.CommandTitle,
            LastCliInvocation = result.CliInvocation,
            Status = result.Summary,
            Tracks = result.LayersOrTracks,
        });
    }
}
