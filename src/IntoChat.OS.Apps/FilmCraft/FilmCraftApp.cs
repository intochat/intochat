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
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Apps.ArtCraft;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.FilmCraft;

[Signal("filmcraft.open")]
public sealed record FilmCraftStudioOpen(string? Sequence = null, string? Mode = null) : Signal;

[Signal("filmcraft.edit")]
public sealed record FilmCraftStudioEdit(string? Prompt = null, string? Sequence = null, string? Mode = null) : Signal;

[Signal("filmcraft.run-command")]
public sealed record FilmCraftRunCommand(string? CommandId = null, string? ParametersJson = null, string? Sequence = null, string? Mode = null) : Signal;

[Signal("filmcraft.export")]
public sealed record FilmCraftExport(string? Preset = null, string? Sequence = null, string? Mode = null) : Signal;

[Signal("filmcraft.switch-mode")]
public sealed record FilmCraftSwitchMode(string? Mode = null) : Signal;

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
        "wasm-ui", "/work/launch-trailer.fcproj", "/work/launch-trailer.cut.mp4", "timeline.razor", "Ready · FilmCraft Studio",
        "filmcraft-cli exec /work/launch-trailer.fcproj --cmd timeline.razor --params '{\"timecode\":\"00:00:03:12\",\"track\":\"V1\"}' --out /work/launch-trailer.cut.mp4",
        "FilmCraft tools connected.",
        ["V2 · Title Overlay (00:00:00:00 -> 00:00:05:00)", "V1 · A-Roll Main Camera (00:00:00:00 -> 00:00:14:00)", "A1 · Master Dialogue Bus (-14 LUFS)"])
    { }

    [Invariant("ExecutionMode is wasm-ui, mcp, or native-linux."), JsonIgnore]
    public bool ValidExecutionMode => ExecutionMode is "wasm-ui" or "mcp" or "native-linux";

    [Invariant("ActiveSequence and Tracks are non-empty."), JsonIgnore]
    public bool HasTimeline => !string.IsNullOrWhiteSpace(ActiveSequence) && Tracks is { Length: > 0 };

    public Rule SupportedExecutionMode(string? mode) =>
        Decision.Rule("invalid_execution_mode", string.IsNullOrWhiteSpace(mode) || mode.Trim().ToLowerInvariant() is not ("wasm-ui" or "mcp" or "native-linux"), "Execution mode must be wasm-ui, mcp, or native-linux.");

    public Decision<FilmCraftAppState> SwitchExecutionMode([Sample("wasm-ui", "mcp", "native-linux", "invalid")] string mode)
    {
        if (Decision.Check("switch-mode", SupportedExecutionMode(mode)) is { } refusal) { return refusal; }
        var normalized = mode.Trim().ToLowerInvariant();
        return Decision.Accept(this with { ExecutionMode = normalized, Status = $"Switched FilmCraft execution backend to {normalized}." });
    }

    [IgnoreExplore]
    public FilmCraftAppState ApplyTimelineOutcome(ArtCraftEngineResult result) => !result.IsValidCommand
        ? this with { ExecutionMode = result.ExecutionMode, LastCommandId = result.CommandId, LastCommandTitle = result.CommandTitle, LastCliInvocation = string.Empty, Status = result.Summary }
        : this with
        {
            ExecutionMode = result.ExecutionMode,
            ActiveSequence = result.InputDocument,
            OutputFile = result.OutputDocument,
            LastCommandId = result.CommandId,
            LastCommandTitle = result.CommandTitle,
            LastCliInvocation = result.CliInvocation,
            Status = result.Summary,
            Tracks = result.LayersOrTracks,
        };
}

[Neuron("app.filmcraft")]
public sealed class FilmCraftApp() : ArtCraftStudioAppCore<FilmCraftAppState>("filmcraft")
{
    protected override string ActiveDocumentLabel => "Sequence";
    public const string AppName = ShippedApps.FilmCraft;

    public static AppBundle Bundle { get; } = new(
        "filmcraft", "1.1.0",
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
            Settings: [], Slots: [], Icon: "film", Dock: 6, Studio: true,
            Toolbar: [new("open", Icon: "link", Label: "Connect editor"), new("export", Icon: "film", Label: "Export MP4")]),
        "", new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(FilmCraftApp).AssemblyQualifiedName,
    };

    [On]
    public async Task Open(FilmCraftStudioOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (mode, seq, webUrl, mcpUri, status) = await ResolveOpenSessionCoreAsync(
            request.Sequence, request.Mode, State.ExecutionMode, State.ActiveSequence, State.WebUrl, State.McpEndpointUri);
        await Show(State with { ExecutionMode = mode, ActiveSequence = seq, WebUrl = webUrl, McpEndpointUri = mcpUri, Status = status });
    }

    [On]
    public Task Edit(FilmCraftStudioEdit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyTimelineExecutionAsync(
            NormalizeTextOrCurrent(request.Prompt, "razor split timeline at 00:00:03:12"), null,
            NormalizeTextOrCurrent(request.Sequence, State.ActiveSequence), NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task RunCommand(FilmCraftRunCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyTimelineExecutionAsync(
            NormalizeTextOrCurrent(request.CommandId, "timeline.razor"), request.ParametersJson,
            NormalizeTextOrCurrent(request.Sequence, State.ActiveSequence), NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task Export(FilmCraftExport request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyTimelineExecutionAsync("sequence.exportMp4", null, NormalizeTextOrCurrent(request.Sequence, State.ActiveSequence), NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task SwitchMode(FilmCraftSwitchMode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextMode = ArtCraftEngineRunner.NormalizeMode(request.Mode);
        return Show(State with { ExecutionMode = nextMode, Status = $"Switched FilmCraft execution backend to {FormatModeLabel(nextMode)}" });
    }

    public Task RazorSplitDefaultAsync() => RunCommand(new FilmCraftRunCommand("timeline.razor", Sequence: State.ActiveSequence, Mode: State.ExecutionMode));
    public Task ExportMp4DefaultAsync() => Export(new FilmCraftExport(Sequence: State.ActiveSequence, Mode: State.ExecutionMode));

    protected override Node Render(FilmCraftAppState state) => RenderStudioShell(
        state.Status, state.ActiveSequence, state.OutputFile, state.WebUrl ?? "http://localhost:18083", state.ExecutionMode,
        "Timeline Tracks", state.Tracks ?? [],
        headerActions:
        [
            GhostButton("WASM").Key("mode-wasm-ui").Small().OnClick<FilmCraftAppState>(x => x.SwitchExecutionMode("wasm-ui")),
            GhostButton("MCP").Key("mode-mcp").Small().OnClick<FilmCraftAppState>(x => x.SwitchExecutionMode("mcp")),
            GhostButton("CLI").Key("mode-native-linux").Small().OnClick<FilmCraftAppState>(x => x.SwitchExecutionMode("native-linux")),
            Button("Razor Split", "scissors").Key("razor-split").Small().OnClick(() => RazorSplitDefaultAsync()),
            PrimaryButton("Export MP4", "film").Key("export-mp4").Small().OnClick(() => ExportMp4DefaultAsync()),
        ],
        inspectorExtras: [Text(state.LastCommandTitle).Muted()]);

    private async Task ApplyTimelineExecutionAsync(string instructionOrCommand, string? explicitParamsJson, string sequence, string mode) =>
        await Show(State.ApplyTimelineOutcome(await ExecuteStudioCommandAsync(instructionOrCommand, explicitParamsJson, sequence, mode)));
}
