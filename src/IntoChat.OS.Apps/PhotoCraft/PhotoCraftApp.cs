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
using System.Reflection;
using System.Text.Json;
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

[Signal("photocraft.toggle-layer")]
public sealed record PhotoCraftToggleLayer(int? LayerIndex = null, string? LayerName = null) : Signal;

[Signal("photocraft.apply-preset")]
public sealed record PhotoCraftApplyPreset(string? Preset = null, string? Document = null, string? Mode = null) : Signal;

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
    string McpEndpointUri = "neuron://local/mcp/photocraft",
    int[]? HiddenLayerIndices = null,
    string? ActivePreset = null)
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
                new("toggle-layer", "Toggle visibility of a raster/adjustment layer by index or name."),
                new("apply-preset", "Apply a local filter preset (warm, vivid, monochrome, cutout, gradient) with instant preview."),
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

    [On]
    public async Task ToggleLayer(PhotoCraftToggleLayer request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baseLayers = State.Layers is { Length: > 0 }
            ? State.Layers
            : ["Subject Layer (100% · Normal)", "Background Layer (Locked · 100%)"];

        var index = request.LayerIndex ?? 0;
        if (request.LayerIndex is null && !string.IsNullOrWhiteSpace(request.LayerName))
        {
            var found = Array.FindIndex(baseLayers, l => l.Contains(request.LayerName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found >= 0) { index = found; }
        }
        index = Math.Clamp(index, 0, baseLayers.Length - 1);

        var hiddenSet = (State.HiddenLayerIndices ?? []).ToHashSet();
        var nowHidden = hiddenSet.Add(index);
        if (!nowHidden)
        {
            hiddenSet.Remove(index);
        }

        var updatedLayers = baseLayers.Select((layer, idx) =>
        {
            var clean = layer.StartsWith("[Hidden] ", StringComparison.Ordinal)
                ? layer["[Hidden] ".Length..]
                : layer;
            return hiddenSet.Contains(idx) ? $"[Hidden] {clean}" : clean;
        }).ToArray();

        var nextPreview = hiddenSet.Count > 0
            ? ArtCraftEngineRunner.CutoutPreviewPngDataUrl
            : ArtCraftEngineRunner.SelectPreviewPngDataUrl(State.LastCommandId);

        var cliParams = JsonSerializer.Serialize(new LayerVisibilityParams(index, !nowHidden), JsonSerializerOptions.Web);
        var entry = new PhotoCraftHistoryEntry(
            $"edit-{State.History.Length + 1}",
            "layer.toggleVisibility",
            $"{(nowHidden ? "Hide" : "Show")} Layer {index + 1}",
            State.ExecutionMode,
            State.OutputDocument,
            $"photocraft-cli run {State.ActiveDocument} --cmd layer.toggleVisibility --params '{cliParams}' --out {State.OutputDocument} --format png");

        await Show(State with
        {
            Layers = updatedLayers,
            HiddenLayerIndices = [.. hiddenSet.Order()],
            PreviewDataUrl = nextPreview,
            LastCommandId = entry.CommandId,
            LastCommandTitle = entry.Title,
            LastCliInvocation = entry.CliInvocation,
            Status = $"{(nowHidden ? "Hid" : "Revealed")} layer {index + 1} ({updatedLayers[index]}) · Local preview updated",
            History = [.. State.History.TakeLast(9), entry],
        });
    }

    [On]
    public async Task ApplyPreset(PhotoCraftApplyPreset request)
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

        await ApplyEngineExecutionAsync(
            command,
            explicitParamsJson: null,
            NormalizeTextOrCurrent(request.Document, State.ActiveDocument),
            NormalizeModeOrCurrent(request.Mode, State.ExecutionMode),
            activePreset: preset);
    }

    protected override View Render(PhotoCraftAppState state)
    {
        var hidden = (state.HiddenLayerIndices ?? []).ToHashSet();
        var presetButtons = new Node[]
        {
            new Button("preset-cutout", "Remove Background", Emit: EmitPhoto(new PhotoCraftRemoveBackground(state.ActiveDocument, state.ExecutionMode)), Tone: ButtonTone.Accent) { Icon = "wand-sparkles", Small = true },
            new Button("preset-warm", "Warm Cinema", Emit: EmitPhoto(new PhotoCraftApplyPreset("warm", state.ActiveDocument, state.ExecutionMode))) { Small = true },
            new Button("preset-vivid", "Vivid Pop", Emit: EmitPhoto(new PhotoCraftApplyPreset("vivid", state.ActiveDocument, state.ExecutionMode))) { Small = true },
            new Button("preset-mono", "Monochrome", Emit: EmitPhoto(new PhotoCraftApplyPreset("monochrome", state.ActiveDocument, state.ExecutionMode))) { Small = true },
            new Button("preset-gradient", "Gradient Canvas", Emit: EmitPhoto(new PhotoCraftApplyPreset("gradient", state.ActiveDocument, state.ExecutionMode))) { Small = true },
        };

        var layerNodes = (state.Layers ?? []).Select((layer, idx) =>
            (Node)new Button(
                $"toggle-layer-{idx}",
                hidden.Contains(idx) ? $"Show: {layer}" : $"Hide: {layer}",
                Emit: EmitPhoto(new PhotoCraftToggleLayer(idx)))
            { Small = true }).ToArray();

        return new View(
            Descriptor.Title,
            new Column(
            [
                new Text($"{state.Status} · {state.ActiveDocument} -> {state.OutputDocument}") { Tone = "muted", Tabular = true },
                new Row(presetButtons) { Gap = 6 },
                new Row(layerNodes) { Gap = 6 },
                new Text($"Preview ready ({(string.IsNullOrEmpty(state.PreviewDataUrl) ? "pending" : "PNG inline")}) · History: {state.History.Length} step(s)") { Tone = "muted", Tabular = true },
                DigitalBrain.UI.Ui.WebBrowser(state.WebUrl ?? "http://localhost:18082", $"{Descriptor.Title} WASM Studio", $"{Descriptor.Id}-wasm", 0, state.ExecutionMode),
            ])
            {
                Gap = 6,
            });
    }

    private sealed record LayerVisibilityParams(int LayerIndex, bool Visible);

    private static Emit EmitPhoto<TSignal>(TSignal signal) where TSignal : Signal
    {
        var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
        return new Emit("app.photocraft", alias, JsonSerializer.SerializeToElement(signal));
    }

    private async Task ApplyEngineExecutionAsync(
        string instructionOrCommand,
        string? explicitParamsJson,
        string targetDoc,
        string mode,
        string? activePreset = null)
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
            PreviewDataUrl = string.IsNullOrWhiteSpace(result.PreviewDataUrl)
                ? ArtCraftEngineRunner.SelectPreviewPngDataUrl(result.CommandId)
                : result.PreviewDataUrl,
            Status = result.Summary,
            Layers = result.LayersOrTracks,
            HiddenLayerIndices = [],
            ActivePreset = activePreset ?? State.ActivePreset,
            History = [.. State.History.TakeLast(9), entry],
        });
    }
}
