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
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Apps.ArtCraft;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;
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
    string? WebUrl = "http://localhost:18082",
    string McpEndpointUri = "neuron://local/mcp/photocraft",
    int[]? HiddenLayerIndices = null,
    string? ActivePreset = null)
{
    public PhotoCraftAppState() : this(
        "wasm-ui", "/work/portrait.png", "/work/portrait.nobg.png", "layer.removeBackground", "Ready · PhotoCraft Studio",
        "photocraft-cli run /work/portrait.png --cmd layer.removeBackground --params '{\"featherPx\":1,\"output\":\"layer-mask\"}' --out /work/portrait.nobg.png --format png",
        ArtCraftEngineRunner.CyanPreviewPngDataUrl, "PhotoCraft tools connected.",
        ["Subject Layer (100% · Normal)", "Background Layer (Locked · 100%)"], [])
    { }

    [Invariant("ExecutionMode is wasm-ui, mcp, or native-linux."), JsonIgnore]
    public bool ValidExecutionMode => ExecutionMode is "wasm-ui" or "mcp" or "native-linux";

    [Invariant("Edit history is bounded at 10 entries."), JsonIgnore]
    public bool HistoryBounded => (History?.Length ?? 0) <= 10;

    [Invariant("PreviewDataUrl is an inline PNG data URL."), JsonIgnore]
    public bool ValidPngPreview => PreviewDataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal);

    public Rule SupportedExecutionMode(string? mode) =>
        Decision.Rule("invalid_execution_mode", string.IsNullOrWhiteSpace(mode) || mode.Trim().ToLowerInvariant() is not ("wasm-ui" or "mcp" or "native-linux"), "Execution mode must be wasm-ui, mcp, or native-linux.");

    public Rule ValidLayerIndex(int index) =>
        Decision.Rule("layer_out_of_range", index < 0 || index >= EffectiveLayers.Length, $"Layer index {index} is outside 0..{EffectiveLayers.Length - 1}.");

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

        var cliParams = JsonSerializer.Serialize(new { layerIndex = index, visible = !nowHidden });
        var currentHistory = History ?? [];
        var entry = new PhotoCraftHistoryEntry(
            $"edit-{currentHistory.Length + 1}", "layer.toggleVisibility", $"{(nowHidden ? "Hide" : "Show")} Layer {index + 1}",
            ExecutionMode, OutputDocument,
            $"photocraft-cli run {ActiveDocument} --cmd layer.toggleVisibility --params '{cliParams}' --out {OutputDocument} --format png");

        return Decision.Accept(this with
        {
            Layers = updatedLayers,
            HiddenLayerIndices = [.. hiddenSet.Order()],
            PreviewDataUrl = hiddenSet.Count > 0 ? ArtCraftEngineRunner.CutoutPreviewPngDataUrl : ArtCraftEngineRunner.SelectPreviewPngDataUrl(LastCommandId),
            LastCommandId = entry.CommandId,
            LastCommandTitle = entry.Title,
            LastCliInvocation = entry.CliInvocation,
            Status = $"{(nowHidden ? "Hid" : "Revealed")} layer {index + 1} ({updatedLayers[index]}) · Local preview updated",
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
            PreviewDataUrl = string.IsNullOrWhiteSpace(result.PreviewDataUrl) ? ArtCraftEngineRunner.SelectPreviewPngDataUrl(result.CommandId) : result.PreviewDataUrl,
            Status = result.Summary,
            Layers = result.LayersOrTracks,
            HiddenLayerIndices = [],
            ActivePreset = activePreset ?? ActivePreset,
            History = [.. currentHistory.TakeLast(9), entry],
        };
    }
}

[Neuron("app.photocraft")]
public sealed class PhotoCraftApp() : ArtCraftStudioAppCore<PhotoCraftAppState>("photocraft")
{
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

        return RenderStudioShell(
            state.Status, state.ActiveDocument, state.OutputDocument, state.WebUrl ?? "http://localhost:18082", state.ExecutionMode,
            "Layer Stack", layers,
            headerActions:
            [
                GhostButton("WASM").Key("mode-wasm-ui").Small().OnClick<PhotoCraftAppState>(x => x.SwitchExecutionMode("wasm-ui")),
                GhostButton("MCP").Key("mode-mcp").Small().OnClick<PhotoCraftAppState>(x => x.SwitchExecutionMode("mcp")),
                GhostButton("CLI").Key("mode-native-linux").Small().OnClick<PhotoCraftAppState>(x => x.SwitchExecutionMode("native-linux")),
                PrimaryButton("Remove Background", "wand-sparkles").Key("preset-cutout").Small().OnClick(() => RemoveBackgroundDefaultAsync()),
            ],
            inspectorExtras:
            [
                MutedText($"Preview ({(string.IsNullOrEmpty(state.PreviewDataUrl) ? "pending" : "PNG inline")}) · History: {(state.History ?? []).Length}/10").Tabular(),
                HStack(Space.Xs)[
                    Button("Warm Cinema").Key("preset-warm").Small().OnClick(() => ApplyQuickPresetAsync("warm")),
                    Button("Vivid Pop").Key("preset-vivid").Small().OnClick(() => ApplyQuickPresetAsync("vivid"))
                ],
                HStack(Space.Xs)[
                    Button("Monochrome").Key("preset-mono").Small().OnClick(() => ApplyQuickPresetAsync("monochrome")),
                    Button("Gradient Canvas").Key("preset-gradient").Small().OnClick(() => ApplyQuickPresetAsync("gradient"))
                ],
                VStack(Space.Xs, [.. layers.Select((layer, idx) => GhostButton(hidden.Contains(idx) ? $"Show: {layer}" : $"Hide: {layer}").Key($"toggle-layer-{idx}").Small().OnClick<PhotoCraftAppState>(x => x.ToggleLayerByIndex(idx)).ToNode())]),
            ]);
    }

    private async Task ApplyEngineExecutionAsync(string instructionOrCommand, string? explicitParamsJson, string targetDoc, string mode, string? activePreset = null) =>
        await Show(State.ApplyEngineOutcome(await ExecuteStudioCommandAsync(instructionOrCommand, explicitParamsJson, targetDoc, mode), activePreset));
}
