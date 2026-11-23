/*
# PhotoCraft
Role: Default
Shipped: true
Operations: open, edit, remove-background, run-command, switch-mode, toggle-layer, apply-preset

Open-source Photoshop-compatible layered image editor (PSD, layers, alpha masks, adjustments) with dual execution:
1. Native Linux CLI mode (photocraft-cli / craft-cli run | serve) for fast headless/cloud execution without WASM overhead.
2. MCP Server mode (IArtCraftStudio + IMcpServer at {brain}/mcp/photocraft) for multi-step agentic tool workflows.
3. Interactive WASM Web UI + instant local Windows raster preview mode for full-card canvas editing.
*/
using IntoChat.OS.Host.Apps;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Apps.ArtCraft;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

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

[Signal("photocraft.toggle-layer")]
public sealed record PhotoCraftToggleLayer(int? LayerIndex = null, string? LayerName = null) : Signal;

[Signal("photocraft.apply-preset")]
public sealed record PhotoCraftApplyPreset(string? Preset = null, string? Document = null, string? Mode = null) : Signal;

public sealed record PhotoCraftHistoryEntry(string Id, string CommandId, string Title, string ExecutionMode, string OutputDocument, string CliInvocation);

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
    string? WebUrl = null,
    string McpEndpointUri = "neuron://local/mcp/photocraft",
    int[]? HiddenLayerIndices = null,
    string? ActivePreset = null)
{
    public PhotoCraftAppState() : this(
        "wasm-ui", "/work/portrait.png", "/work/portrait.nobg.png", "layer.removeBackground", "Ready · PhotoCraft Studio",
        string.Empty,
        string.Empty, "PhotoCraft WASM Studio ready.",
        ["Subject Layer (100% · Normal)", "Background Layer (Locked · 100%)"], [], "/artcraft/photocraft/index.html")
    { }

    [Invariant("ExecutionMode is wasm-ui, mcp, or native-linux."), JsonIgnore]
    public bool ValidExecutionMode => ExecutionMode is "wasm-ui" or "mcp" or "native-linux";

    [Invariant("Edit history is bounded at 10 entries."), JsonIgnore]
    public bool HistoryBounded => (History?.Length ?? 0) <= 10;

    [Invariant("PreviewDataUrl is empty or an inline PNG data URL."), JsonIgnore]
    public bool ValidPngPreview => string.IsNullOrEmpty(PreviewDataUrl) || PreviewDataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal);

    public Rule SupportedExecutionMode(string? mode) => Decision.Rule(
        "invalid_execution_mode",
        mode,
        static m => string.IsNullOrWhiteSpace(m) || m.Trim().ToLowerInvariant() is not ("wasm-ui" or "mcp" or "native-linux"),
        "Execution mode must be wasm-ui, mcp, or native-linux.");

    public Rule ValidLayerIndex(int index) => Decision.Rule(
        "layer_out_of_range",
        (Index: index, Count: EffectiveLayers.Length),
        static x => x.Index < 0 || x.Index >= x.Count,
        static x => $"Layer index {x.Index} is outside 0..{x.Count - 1}.");

    private string[] EffectiveLayers => Layers is { Length: > 0 } ? Layers : ["Subject Layer (100% · Normal)", "Background Layer (Locked · 100%)"];

    public Decision<PhotoCraftAppState> SwitchExecutionMode([Sample("wasm-ui", "mcp", "native-linux", "invalid")] string mode)
    {
        if (Decision.Check("switch-mode", SupportedExecutionMode(mode)) is { } refusal) { return refusal; }
        var normalized = mode.Trim().ToLowerInvariant();
        return Decision.Accept(this with { ExecutionMode = normalized, Status = $"Switched PhotoCraft execution backend to {normalized}." });
    }

    public Decision<PhotoCraftAppState> ToggleLayerByIndex([Sample(0, 1, 9)] int index)
    {
        if (Decision.Check("toggle-layer", ValidLayerIndex(index)) is { } refusal) { return refusal; }

        var baseLayers = EffectiveLayers;
        var hiddenSet = (HiddenLayerIndices ?? []).ToHashSet();
        var nowHidden = hiddenSet.Add(index);
        if (!nowHidden) { hiddenSet.Remove(index); }

        var updatedLayers = baseLayers.Select((layer, idx) =>
        {
            var clean = layer.StartsWith("[Hidden] ", StringComparison.Ordinal) ? layer["[Hidden] ".Length..] : layer;
            return hiddenSet.Contains(idx) ? $"[Hidden] {clean}" : clean;
        }).ToArray();

        var currentHistory = History ?? [];
        var entry = new PhotoCraftHistoryEntry(
            $"edit-{currentHistory.Length + 1}", "layer.toggleVisibility", $"{(nowHidden ? "Hide" : "Show")} Layer {index + 1}",
            ExecutionMode, OutputDocument,
            string.Empty);

        return Decision.Accept(this with
        {
            Layers = updatedLayers,
            HiddenLayerIndices = [.. hiddenSet.Order()],
            LastCommandId = entry.CommandId,
            LastCommandTitle = entry.Title,
            LastCliInvocation = string.Empty,
            Status = $"{(nowHidden ? "Hid" : "Revealed")} layer {index + 1} ({updatedLayers[index]})",
            History = [.. currentHistory.TakeLast(9), entry],
        });
    }

    [IgnoreExplore]
    public PhotoCraftAppState ApplyEngineOutcome(ArtCraftEngineResult result, string? activePreset)
    {
        if (!result.IsValidCommand)
        {
            return this with { ExecutionMode = result.ExecutionMode, LastCommandId = result.CommandId, LastCommandTitle = result.CommandTitle, LastCliInvocation = string.Empty, Status = result.Summary };
        }

        var currentHistory = History ?? [];
        var entry = new PhotoCraftHistoryEntry($"edit-{currentHistory.Length + 1}", result.CommandId, result.CommandTitle, result.ExecutionMode, result.OutputDocument, result.CliInvocation);
        return this with
        {
            ExecutionMode = result.ExecutionMode,
            ActiveDocument = result.InputDocument,
            OutputDocument = result.OutputDocument,
            LastCommandId = result.CommandId,
            LastCommandTitle = result.CommandTitle,
            LastCliInvocation = result.CliInvocation,
            PreviewDataUrl = result.PreviewDataUrl ?? string.Empty,
            Status = result.Summary,
            Layers = result.LayersOrTracks,
            HiddenLayerIndices = [],
            ActivePreset = activePreset ?? ActivePreset,
            History = [.. currentHistory.TakeLast(9), entry],
        };
    }
}

[Neuron("app.photocraft")]
public sealed class PhotoCraftApp : ArtCraftStudioAppCore<PhotoCraftAppState>
{
    public PhotoCraftApp() : this(null) { }

    [ActivatorUtilitiesConstructor]
    public PhotoCraftApp(ILogger<PhotoCraftApp>? logger = null) : base("photocraft", logger) { }

    public const string AppName = ShippedApps.PhotoCraft;

    public static AppBundle Bundle { get; } = new(
        "photocraft", "1.1.0",
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
                new("toggle-layer", "Toggle visibility of a raster/adjustment layer by index or name."),
                new("apply-preset", "Apply a local filter preset (warm, vivid, monochrome, cutout, gradient) with instant preview."),
            ],
            Settings: [], Slots: [], Icon: "image", Dock: 5, Studio: true,
            Toolbar: [new("open", Icon: "link", Label: "Connect editor"), new("remove-background", Icon: "wand-sparkles", Label: "Remove Background")]),
        "", new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(PhotoCraftApp).AssemblyQualifiedName,
    };

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        var preferredMode = await ResolvePreferredExecutionModeAsync();
        if (string.IsNullOrWhiteSpace(State.WebUrl) || IsLegacyUnhostedWasmState(State.ExecutionMode, State.WebUrl))
        {
            await Show(State with
            {
                ExecutionMode = preferredMode,
                WebUrl = Descriptor.StaticWebPath,
                Status = $"PhotoCraft studio ({FormatModeLabel(preferredMode)}) · Active: {State.ActiveDocument}",
            });
        }
    }

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
            NormalizeTextOrCurrent(request.Prompt, "remove background from portrait.png"), null,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument), NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task RemoveBackground(PhotoCraftRemoveBackground request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetDoc = NormalizeTextOrCurrent(request.Document, State.ActiveDocument);
        return ApplyEngineExecutionAsync($"remove background from {targetDoc}", null, targetDoc, NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task RunCommand(PhotoCraftRunCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyEngineExecutionAsync(
            NormalizeTextOrCurrent(request.CommandId, "layer.removeBackground"), request.ParametersJson,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument), NormalizeModeOrCurrent(request.Mode, State.ExecutionMode));
    }

    [On]
    public Task SwitchMode(PhotoCraftSwitchMode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextMode = ArtCraftEngineRunner.NormalizeMode(request.Mode);
        return Show(State with { ExecutionMode = nextMode, Status = $"Switched PhotoCraft execution backend to {FormatModeLabel(nextMode)}" });
    }

    [On]
    public Task ToggleLayer(PhotoCraftToggleLayer request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baseLayers = State.Layers is { Length: > 0 } ? State.Layers : ["Subject Layer (100% · Normal)", "Background Layer (Locked · 100%)"];
        var index = request.LayerIndex ?? 0;
        if (request.LayerIndex is null && !string.IsNullOrWhiteSpace(request.LayerName))
        {
            var found = Array.FindIndex(baseLayers, l => l.Contains(request.LayerName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found >= 0) { index = found; }
        }
        return ApplyDecisionAsync(State.ToggleLayerByIndex(Math.Clamp(index, 0, baseLayers.Length - 1)));
    }

    [On]
    public Task ApplyPreset(PhotoCraftApplyPreset request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preset = NormalizeTextOrCurrent(request.Preset, "warm").ToLowerInvariant();
        var command = preset switch
        {
            "vivid" or "pop" => "preset.vivid",
            "mono" or "monochrome" or "grayscale" or "bw" => "image.adjustments.desaturate",
            "gradient" or "poster" => "layer.newFillLayer.gradient",
            "cutout" or "nobg" or "remove-background" => "layer.removeBackground",
            "levels" or "auto" => "adjust levels photo",
            _ => "preset.warm",
        };
        return ApplyEngineExecutionAsync(command, null, NormalizeTextOrCurrent(request.Document, State.ActiveDocument), NormalizeModeOrCurrent(request.Mode, State.ExecutionMode), preset);
    }

    public Task RemoveBackgroundDefaultAsync() => RemoveBackground(new PhotoCraftRemoveBackground(State.ActiveDocument, State.ExecutionMode));
    public Task ApplyQuickPresetAsync(string preset) => ApplyPreset(new PhotoCraftApplyPreset(preset, State.ActiveDocument, State.ExecutionMode));

    protected override Node Render(PhotoCraftAppState state)
    {
        var hidden = (state.HiddenLayerIndices ?? []).ToHashSet();
        var layers = state.Layers ?? [];
        var history = state.History ?? [];

        var presetBar = Toolbar(
            SectionHeader("Quick Presets"),
            (state.ActivePreset == "warm" ? PrimaryButton("Warm Cinema") : Button("Warm Cinema")).Key("preset-warm").Small().OnClick(() => ApplyQuickPresetAsync("warm")),
            (state.ActivePreset == "vivid" ? PrimaryButton("Vivid Pop") : Button("Vivid Pop")).Key("preset-vivid").Small().OnClick(() => ApplyQuickPresetAsync("vivid")),
            (state.ActivePreset == "monochrome" ? PrimaryButton("Monochrome") : Button("Monochrome")).Key("preset-mono").Small().OnClick(() => ApplyQuickPresetAsync("monochrome")),
            (state.ActivePreset == "gradient" ? PrimaryButton("Gradient Canvas") : Button("Gradient Canvas")).Key("preset-gradient").Small().OnClick(() => ApplyQuickPresetAsync("gradient"))
        );

        var studioCanvas = VStack(Space.Md)[
            presetBar,
            !string.IsNullOrWhiteSpace(state.PreviewDataUrl)
                ? ImageView(
                    state.PreviewDataUrl,
                    $"Canvas Preview · {state.LastCommandTitle} ({state.ActiveDocument} -> {state.OutputDocument})",
                    height: 300)
                : Surface(SurfaceKind.Elevated, "Canvas Preview", "Connect PhotoCraft MCP in Settings to render live previews.")[
                    MutedText($"No raster preview rendered ({state.ActiveDocument} -> {state.OutputDocument}).")
                ],
            !string.IsNullOrWhiteSpace(state.LastCliInvocation)
                ? CodeBlock($"Execution Log · {FormatModeLabel(state.ExecutionMode)}", state.LastCliInvocation, "bash", maxHeight: 140)
                : null,
            history.Length > 0
                ? Table(
                    $"Edit History ({history.Length}/10)",
                    ["Step", "Operation", "Backend", "Output"],
                    [.. history.Select(h => (IReadOnlyList<string>)[h.Id, h.Title, h.ExecutionMode, h.OutputDocument])])
                : null
        ];

        return RenderStudioShell(
            state.Status, state.ActiveDocument, state.OutputDocument, state.WebUrl ?? Descriptor.StaticWebPath, state.ExecutionMode,
            "Layer Stack", [],
            headerActions:
            [
                (state.ExecutionMode == "mcp" ? Button("MCP") : GhostButton("MCP")).Key("mode-mcp").Small().OnClick<PhotoCraftAppState>(x => x.SwitchExecutionMode("mcp")),
                (state.ExecutionMode == "native-linux" ? Button("CLI") : GhostButton("CLI")).Key("mode-native-linux").Small().OnClick<PhotoCraftAppState>(x => x.SwitchExecutionMode("native-linux")),
                (state.ExecutionMode == "wasm-ui" ? Button("WASM") : GhostButton("WASM")).Key("mode-wasm-ui").Small().OnClick<PhotoCraftAppState>(x => x.SwitchExecutionMode("wasm-ui")),
                PrimaryButton("Remove Background", "wand-sparkles").Key("preset-cutout").Small().OnClick(() => RemoveBackgroundDefaultAsync()),
            ],
            inspectorExtras:
            [
                MutedText($"Preview ({(string.IsNullOrEmpty(state.PreviewDataUrl) ? "pending" : "PNG inline")}) · History: {history.Length}/10").Tabular(),
                state.ExecutionMode == "wasm-ui" ? presetBar : null,
                SectionHeader("Layers", $"{layers.Length} layer(s)"),
                VStack(Space.Xs, [.. layers.Select((layer, idx) =>
                {
                    var cleanLayer = layer.StartsWith("[Hidden] ", StringComparison.Ordinal) ? layer["[Hidden] ".Length..] : layer;
                    var shortLabel = cleanLayer.Length > 28 ? cleanLayer[..25] + "…" : cleanLayer;
                    return GhostButton(
                            hidden.Contains(idx) ? $"Show · {shortLabel}" : $"Hide · {shortLabel}",
                            hidden.Contains(idx) ? "eye-off" : "eye")
                        .Key($"toggle-layer-{idx}")
                        .Small()
                        .OnClick<PhotoCraftAppState>(x => x.ToggleLayerByIndex(idx))
                        .ToNode();
                })]),
            ],
            nativeCanvas: studioCanvas,
            cliInvocation: state.LastCliInvocation,
            mcpUri: state.McpEndpointUri);
    }

    private async Task ApplyEngineExecutionAsync(string instructionOrCommand, string? explicitParamsJson, string targetDoc, string mode, string? activePreset = null) =>
        await Show(State.ApplyEngineOutcome(await ExecuteStudioCommandAsync(instructionOrCommand, explicitParamsJson, targetDoc, mode), activePreset));
}
