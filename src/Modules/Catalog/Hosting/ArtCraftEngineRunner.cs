using System.IO.Compression;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Modules.Apps;

public static class ArtCraftEngineRunner
{
    public static readonly string CyanPreviewPngDataUrl = BuildStudioPreviewPng("cyan");
    public static readonly string GradientPreviewPngDataUrl = BuildStudioPreviewPng("gradient");
    public static readonly string CutoutPreviewPngDataUrl = BuildStudioPreviewPng("cutout");
    public static readonly string MonochromePreviewPngDataUrl = BuildStudioPreviewPng("mono");
    public static readonly string WarmPresetPngDataUrl = BuildStudioPreviewPng("warm");
    public static readonly string VividPresetPngDataUrl = BuildStudioPreviewPng("vivid");

    private static readonly (string[] Terms, string CmdId, string Title, string DefaultJson, string Suffix, string[] Layers)[] PhotoCommands =
    [
        (["remove background", "remove-background", "background removal", "remove the background", "cutout", "layer.removeBackground"],
            "layer.removeBackground", "Remove Background (AI Subject Alpha Mask)", "{\"featherPx\":1,\"output\":\"layer-mask\"}", "nobg.png",
            ["Subject Cutout (Alpha Mask · 100%)", "Original Background (Hidden · 0%)"]),
        (["select subject", "select.subject"],
            "select.subject", "Select Subject (Marching Ants Selection)", "{\"refineEdge\":true}", "subject.png",
            ["Active Selection (Subject Contour)", "Background Layer (Locked · 100%)"]),
        (["select sky", "select.sky"],
            "select.sky", "Select Sky", "{}", "sky.png",
            ["Sky Selection Mask (Active)", "Foreground Landscape (100%)"]),
        (["desaturate", "grayscale", "black and white", "monochrome", "image.adjustments.desaturate"],
            "image.adjustments.desaturate", "Desaturate (Monochrome Tonemap)", "{}", "mono.png",
            ["Desaturate Adjustment (Luminance · 100%)", "Base Raster Layer (100%)"]),
        (["warm", "vintage", "golden hour", "sepia", "preset.warm"],
            "preset.warm", "Apply Warm Cinema Filter Preset", "{\"temperature\":18,\"tint\":4,\"contrast\":8}", "warm.png",
            ["Warm Cinema LUT Adjustment (100% · Soft Light)", "Base Raster Layer (100%)"]),
        (["vivid", "pop", "vibrance", "saturate", "preset.vivid"],
            "preset.vivid", "Apply Vivid Studio Filter Preset", "{\"vibrance\":28,\"clarity\":14}", "vivid.png",
            ["Vivid Studio Adjustment (100% · Normal)", "Base Raster Layer (100%)"]),
        (["flip", "mirror", "image.imageRotation.flipCanvasHorizontal"],
            "image.imageRotation.flipCanvasHorizontal", "Flip Canvas Horizontal", "{}", "flipped.png",
            ["Flipped Composite Layer (100%)"]),
        (["rotate", "90cw", "image.imageRotation.90cw"],
            "image.imageRotation.90cw", "Rotate Canvas 90° Clockwise", "{}", "rot90.png",
            ["Rotated Canvas Layer (90° CW · 100%)"]),
    ];

    private static readonly (string[] Terms, string CmdId, string Title, string DefaultJson, string Suffix, string[] Tracks)[] FilmCommands =
    [
        (["lumetri", "color", "grade", "color.lumetri.auto"],
            "color.lumetri.auto", "Lumetri Color Auto Grade (Cinema Teal/Orange)", "{\"temperature\":8,\"contrast\":14,\"saturation\":108}", "graded.mp4",
            ["V2 · Adjustment Layer (Lumetri Cinema Grade)", "V1 · A-Roll Main Camera", "A1 · Dialogue Stem"]),
        (["trim", "ripple", "timeline.rippleTrim"],
            "timeline.rippleTrim", "Ripple Trim Out-Point (-24 frames)", "{\"track\":\"V1\",\"deltaFrames\":-24}", "trimmed.mp4",
            ["V1 · A-Roll Clip 1 (Ripple Trimmed)", "A1 · Master Stereo Mix"]),
        (["export", "render", "mp4", "sequence.exportMp4"],
            "sequence.exportMp4", "Export Sequence (H.264 1080p60 MP4)", "{\"preset\":\"h264-1080p60\",\"bitrateMbps\":18}", "1080p60.mp4",
            ["V2 · Lower Third Title Overlay", "V1 · Master Edit Sequence (Rendered 1080p60)", "A1 · Master Stereo Bus"]),
        (["razor", "split", "cut", "timeline", "clip", "playhead", "00:", ShippedApps.FilmCraft, "videocraft", "timeline.razor"],
            "timeline.razor", "Razor Split Clip at Playhead (00:00:03:12)", "{\"timecode\":\"00:00:03:12\",\"track\":\"V1\"}", "cut.mp4",
            ["V1 · Clip 1A (00:00:00:00 -> 00:00:03:12)", "V1 · Clip 1B [Split] (00:00:03:12 -> 00:00:12:00)", "A1 · Synced Dialogue Track"]),
    ];

    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "mcp" or "mcp-server" => "mcp",
        "wasm" or "wasm-ui" or "web" => "wasm-ui",
        _ => "native-linux",
    };

    public static bool TryResolvePhotoCraftCommand(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        out (string CommandId, string Title, string ParamsJson, string InputDoc, string OutputDoc, string[] Layers) resolved)
    {
        var extracted = string.IsNullOrWhiteSpace(instructionOrCommand) ? null : ExtractFileName(instructionOrCommand);
        var doc = EnsureWorkPath(extracted ?? (string.IsNullOrWhiteSpace(currentDocument) ? "/work/portrait.png" : currentDocument.Trim()));
        if (string.IsNullOrWhiteSpace(instructionOrCommand))
        {
            resolved = ("clarification.required", "Clarification Required", "{}", doc, string.Empty, []);
            return false;
        }

        var raw = instructionOrCommand.Trim();
        var stem = NonEmptyStem(doc, "canvas");

        if (ContainsAny(raw, "gradient", "layer.newFillLayer.gradient", "poster", "sunset", "512x512", "new canvas", "create image"))
        {
            resolved = (
                "layer.newFillLayer.gradient",
                "New Fill Layer: Gradient (Blue -> Orange)",
                DefaultJson(explicitParamsJson, "{\"width\":512,\"height\":512,\"from\":\"#2563EB\",\"to\":\"#F97316\",\"angle\":135,\"style\":\"linear\"}"),
                "/work/gradient-512x512.psd",
                "/work/gradient-512x512.png",
                ["Gradient Fill 1 (#2563EB -> #F97316 · 135°)", "Canvas 512x512 (Background)"]);
            return true;
        }

        foreach (var entry in PhotoCommands)
        {
            if (ContainsAny(raw, entry.Terms))
            {
                resolved = (entry.CmdId, entry.Title, DefaultJson(explicitParamsJson, entry.DefaultJson), doc, $"/work/{stem}.{entry.Suffix}", entry.Layers);
                return true;
            }
        }

        var hasImageContext = (extracted is not null && IsImageExtension(extracted))
            || ContainsAny(raw, ShippedApps.PhotoCraft, "photo", "image", "canvas");

        if (ContainsAny(raw, "layer.", "image.adjustments.")
            || (hasImageContext && ContainsAny(raw, "adjust", "resize", "crop", "blur", "sharpen", "brightness", "contrast", "levels", "curves", "mask", "edit")))
        {
            resolved = (
                "image.adjustments.levels",
                $"Adjust Levels & Tone ({stem})",
                DefaultJson(explicitParamsJson, "{\"auto\":true}"),
                doc,
                $"/work/{stem}.adjusted.png",
                ["Levels Adjustment Layer (Auto Tone · 100%)", "Source Layer (100%)"]);
            return true;
        }

        resolved = ("clarification.required", "Clarification Required", "{}", doc, string.Empty, []);
        return false;
    }

    public static bool IsExplicitPhotoCraftCommand(string? instructionOrCommand)
        => TryResolvePhotoCraftCommand(instructionOrCommand, null, null, out _);

    public static bool IsExplicitFilmCraftCommand(string? instructionOrCommand)
        => TryResolveFilmCraftCommand(instructionOrCommand, null, null, out _);

    public static bool TryResolveFilmCraftCommand(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentSequence,
        out (string CommandId, string Title, string ParamsJson, string InputDoc, string OutputDoc, string[] Tracks) resolved)
    {
        var extracted = string.IsNullOrWhiteSpace(instructionOrCommand) ? null : ExtractFileName(instructionOrCommand);
        var doc = EnsureWorkPath(extracted ?? (string.IsNullOrWhiteSpace(currentSequence) ? "/work/launch-trailer.fcproj" : currentSequence.Trim()));
        if (string.IsNullOrWhiteSpace(instructionOrCommand))
        {
            resolved = ("clarification.required", "Clarification Required", "{}", doc, string.Empty, []);
            return false;
        }

        var raw = instructionOrCommand.Trim();
        var stem = NonEmptyStem(doc, "sequence");

        foreach (var entry in FilmCommands)
        {
            if (ContainsAny(raw, entry.Terms))
            {
                resolved = (entry.CmdId, entry.Title, DefaultJson(explicitParamsJson, entry.DefaultJson), doc, $"/work/{stem}.{entry.Suffix}", entry.Tracks);
                return true;
            }
        }

        resolved = ("clarification.required", "Clarification Required", "{}", doc, string.Empty, []);
        return false;
    }

    public static async Task<ArtCraftEngineResult> ExecuteAsync(
        string app,
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        string executionMode,
        IArtCraftStudio? studio,
        Func<string, IMcpServer?>? mcpResolver,
        Action<Exception, string>? onRecoverableError = null,
        IFilesVfsService? vfs = null)
    {
        var descriptor = ArtCraftCatalog.Get(app);
        if (descriptor.Id == ShippedApps.PhotoCraft)
        {
            return await ExecutePhotoCraftAsync(instructionOrCommand, explicitParamsJson, currentDocument, executionMode, studio, mcpResolver, onRecoverableError, vfs).ConfigureAwait(false);
        }
        if (descriptor.Id == ShippedApps.FilmCraft)
        {
            return await ExecuteFilmCraftAsync(instructionOrCommand, explicitParamsJson, currentDocument, executionMode, studio, mcpResolver, onRecoverableError, vfs).ConfigureAwait(false);
        }

        var mode = NormalizeMode(executionMode);
        var inputDoc = await ResolveStudioInputDocumentAsync(descriptor, instructionOrCommand, currentDocument, vfs, onRecoverableError).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(instructionOrCommand))
        {
            return CreateClarificationResult(descriptor.Id, mode, inputDoc, $"Specify a {descriptor.Title} command or instruction.");
        }

        var raw = instructionOrCommand.Trim();
        var isDotCommand = raw.Contains('.', StringComparison.Ordinal) && !raw.Contains(' ', StringComparison.Ordinal);
        var cmdId = isDotCommand ? raw : descriptor.DefaultCommandId;
        var title = isDotCommand ? $"{descriptor.Title} · {raw}" : descriptor.DefaultCommandTitle;
        var paramsJson = DefaultJson(explicitParamsJson, descriptor.DefaultParametersJson);
        var stem = NonEmptyStem(inputDoc, descriptor.Id);
        var outExt = Path.GetExtension(descriptor.DefaultOutputDocument);
        var outputDoc = $"/work/{stem}.processed{(string.IsNullOrWhiteSpace(outExt) ? ".out" : outExt)}";
        var cliInvocation = $"{descriptor.CliBinary} run {inputDoc} --cmd {cmdId} --params '{paramsJson}' --out {outputDoc}";

        return await RunAndPersistStudioCommandAsync(
            descriptor, mode, cmdId, title, paramsJson, inputDoc, outputDoc, cliInvocation,
            descriptor.DefaultLayersOrTracks, "Input", isNewGradient: false,
            studio, mcpResolver, onRecoverableError, vfs).ConfigureAwait(false);
    }

    public static async Task<ArtCraftEngineResult> ExecutePhotoCraftAsync(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        string executionMode,
        IArtCraftStudio? studio,
        Func<string, IMcpServer?>? mcpResolver,
        Action<Exception, string>? onRecoverableError = null,
        IFilesVfsService? vfs = null)
    {
        var descriptor = ArtCraftCatalog.Get(ShippedApps.PhotoCraft);
        var mode = NormalizeMode(executionMode);
        var resolvedCurrentDoc = await ResolveInputDocumentFromVfsAsync(instructionOrCommand, currentDocument, vfs, isVideo: false, onRecoverableError).ConfigureAwait(false);

        if (!TryResolvePhotoCraftCommand(instructionOrCommand, explicitParamsJson, resolvedCurrentDoc, out var resolved))
        {
            return CreateClarificationResult(ShippedApps.PhotoCraft, mode, resolved.InputDoc, $"Unrecognized photocraft command '{instructionOrCommand}'. Specify a valid operation and target file in Files.");
        }

        var (cmdId, title, paramsJson, inputDoc, outputDoc, layers) = resolved;
        var isNewGradient = cmdId == "layer.newFillLayer.gradient";
        if (vfs is not null && !isNewGradient)
        {
            inputDoc = await TryResolveExactVfsPathAsync(inputDoc, vfs, "PhotoCraft input file", onRecoverableError).ConfigureAwait(false);
        }

        var cliInvocation = isNewGradient
            ? $"photocraft-cli run --new '{{\"width\":512,\"height\":512}}' --cmd {cmdId} --params '{paramsJson}' --out {outputDoc} --format png"
            : $"photocraft-cli run {inputDoc} --cmd {cmdId} --params '{paramsJson}' --out {outputDoc} --format png";

        return await RunAndPersistStudioCommandAsync(
            descriptor, mode, cmdId, title, paramsJson, inputDoc, outputDoc, cliInvocation,
            layers, "Input", isNewGradient, studio, mcpResolver, onRecoverableError, vfs).ConfigureAwait(false);
    }

    public static async Task<ArtCraftEngineResult> ExecuteFilmCraftAsync(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentSequence,
        string executionMode,
        IArtCraftStudio? studio,
        Func<string, IMcpServer?>? mcpResolver,
        Action<Exception, string>? onRecoverableError = null,
        IFilesVfsService? vfs = null)
    {
        var descriptor = ArtCraftCatalog.Get(ShippedApps.FilmCraft);
        var mode = NormalizeMode(executionMode);
        var resolvedCurrentSeq = await ResolveInputDocumentFromVfsAsync(instructionOrCommand, currentSequence, vfs, isVideo: true, onRecoverableError).ConfigureAwait(false);

        if (!TryResolveFilmCraftCommand(instructionOrCommand, explicitParamsJson, resolvedCurrentSeq, out var resolved))
        {
            return CreateClarificationResult(ShippedApps.FilmCraft, mode, resolved.InputDoc, $"Unrecognized filmcraft command '{instructionOrCommand}'. Specify a valid operation and target file in Files.");
        }

        var (cmdId, title, paramsJson, inputDoc, outputDoc, tracks) = resolved;
        if (vfs is not null)
        {
            inputDoc = await TryResolveExactVfsPathAsync(inputDoc, vfs, "FilmCraft sequence", onRecoverableError).ConfigureAwait(false);
        }

        var cliInvocation = $"filmcraft-cli exec {inputDoc} --cmd {cmdId} --params '{paramsJson}' --out {outputDoc}";
        return await RunAndPersistStudioCommandAsync(
            descriptor, mode, cmdId, title, paramsJson, inputDoc, outputDoc, cliInvocation,
            tracks, "Sequence", isNewGradient: false, studio, mcpResolver, onRecoverableError, vfs).ConfigureAwait(false);
    }

    private static ArtCraftEngineResult CreateClarificationResult(string appId, string mode, string inputDoc, string summary) => new(
        appId, mode, "clarification.required", "Clarification Required", "{}",
        inputDoc, string.Empty, string.Empty, summary, string.Empty, [],
        IsValidCommand: false, OutputNeuronUri: null);

    private static async Task<ArtCraftEngineResult> RunAndPersistStudioCommandAsync(
        ArtCraftAppDescriptor descriptor,
        string mode,
        string cmdId,
        string title,
        string paramsJson,
        string inputDoc,
        string outputDoc,
        string cliInvocation,
        string[] layersOrTracks,
        string inputLabel,
        bool isNewGradient,
        IArtCraftStudio? studio,
        Func<string, IMcpServer?>? mcpResolver,
        Action<Exception, string>? onRecoverableError,
        IFilesVfsService? vfs)
    {
        var previewUrl = SelectPreviewPngDataUrl(cmdId, isNewGradient);
        var modeBadge = mode switch
        {
            "mcp" => $"MCP Server ({descriptor.Id}-mcp)",
            "wasm-ui" => descriptor.Id == ShippedApps.FilmCraft ? "WASM Web UI + Page Bridge" : "WASM Web UI + Engine",
            _ => $"Native Linux CLI ({descriptor.CliBinary})",
        };

        if (mode == "mcp" && studio is not null && mcpResolver is not null)
        {
            try
            {
                var session = await studio.Open(descriptor.Id);
                if (mcpResolver(session.McpKey) is { } mcpServer)
                {
                    if (descriptor.Id == ShippedApps.PhotoCraft)
                    {
                        await mcpServer.Run(
                            isNewGradient ? "doc_new" : "doc_open",
                            isNewGradient ? "{\"width\":512,\"height\":512,\"name\":\"Gradient\"}" : JsonSerializer.Serialize(new { path = inputDoc }));
                        await mcpServer.Run("command_run", $"{{\"id\":\"{cmdId}\",\"params\":{paramsJson}}}");
                        var previewResult = await mcpServer.Run("render_preview", "{\"format\":\"png\"}");
                        if (!previewResult.IsError && previewResult.Blobs is { Length: > 0 } blobs && !string.IsNullOrWhiteSpace(blobs[0].Data))
                        {
                            previewUrl = $"data:{blobs[0].MediaType};base64,{blobs[0].Data}";
                        }
                        await mcpServer.Run("doc_save", JsonSerializer.Serialize(new { path = outputDoc, format = "png" }));
                    }
                    else
                    {
                        await mcpServer.Run("command_run", $"{{\"id\":\"{cmdId}\",\"params\":{paramsJson}}}");
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, $"{descriptor.Title} MCP server is optional when running offline or in TestOS.");
            }
        }

        string? outputNeuronUri = null;
        if (vfs is not null && !string.IsNullOrWhiteSpace(outputDoc) && descriptor.Id == ShippedApps.PhotoCraft && !string.IsNullOrWhiteSpace(previewUrl))
        {
            try
            {
                var bytes = DecodeDataUrlBytes(previewUrl, $"PhotoCraft PNG artifact ({cmdId}) -> {outputDoc}");
                if (bytes.Length > 0)
                {
                    outputNeuronUri = (await vfs.WriteFileAsync(outputDoc, bytes).ConfigureAwait(false)).NeuronUri;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, $"Could not persist {descriptor.Title} output artifact to Files VFS.");
            }
        }

        var summary = $"Executed `{cmdId}` ({title}) via **{modeBadge}** · {inputLabel}: `{inputDoc}` -> Output: `{outputDoc}`";
        return new ArtCraftEngineResult(
            descriptor.Id, mode, cmdId, title, paramsJson, inputDoc, outputDoc,
            cliInvocation, summary, previewUrl, layersOrTracks,
            IsValidCommand: true, OutputNeuronUri: outputNeuronUri);
    }

    private static async Task<string> ResolveStudioInputDocumentAsync(
        ArtCraftAppDescriptor descriptor,
        string? instructionOrCommand,
        string? currentDocument,
        IFilesVfsService? vfs,
        Action<Exception, string>? onRecoverableError)
    {
        var extracted = string.IsNullOrWhiteSpace(instructionOrCommand) ? null : ExtractFileName(instructionOrCommand);
        var inputDoc = EnsureWorkPath(extracted ?? (string.IsNullOrWhiteSpace(currentDocument) ? descriptor.DefaultDocument : currentDocument.Trim()));
        if (vfs is null) { return inputDoc; }

        try
        {
            if (await vfs.ResolveFileAsync(inputDoc).ConfigureAwait(false) is { } vfsEntry) { return vfsEntry.VfsPath; }
            if (string.Equals(inputDoc, descriptor.DefaultDocument, StringComparison.OrdinalIgnoreCase))
            {
                var files = await vfs.ListFilesAsync().ConfigureAwait(false);
                if (files.FirstOrDefault(f => descriptor.SupportedExtensions.Any(ext => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) is { } candidate)
                {
                    return candidate.VfsPath;
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            onRecoverableError?.Invoke(error, $"Could not resolve {descriptor.Title} input file against Files VFS.");
        }

        return inputDoc;
    }

    private static async Task<string> TryResolveExactVfsPathAsync(
        string inputDoc, IFilesVfsService vfs, string label, Action<Exception, string>? onRecoverableError)
    {
        try
        {
            if (await vfs.ResolveFileAsync(inputDoc).ConfigureAwait(false) is { } vfsEntry) { return vfsEntry.VfsPath; }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            onRecoverableError?.Invoke(error, $"Could not resolve {label} against Files VFS.");
        }

        return inputDoc;
    }

    private static async Task<string?> ResolveInputDocumentFromVfsAsync(
        string? instructionOrCommand,
        string? currentDocument,
        IFilesVfsService? vfs,
        bool isVideo,
        Action<Exception, string>? onRecoverableError)
    {
        if (vfs is null) { return currentDocument; }

        try
        {
            var extracted = string.IsNullOrWhiteSpace(instructionOrCommand) ? null : ExtractFileName(instructionOrCommand);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return (await vfs.ResolveFileAsync(extracted).ConfigureAwait(false))?.VfsPath ?? currentDocument;
            }

            if (!string.IsNullOrWhiteSpace(currentDocument))
            {
                if (await vfs.ResolveFileAsync(currentDocument).ConfigureAwait(false) is { } matchedCurrent) { return matchedCurrent.VfsPath; }
                var defaultDoc = isVideo ? "/work/launch-trailer.fcproj" : "/work/portrait.png";
                if (!string.Equals(currentDocument, defaultDoc, StringComparison.OrdinalIgnoreCase)) { return currentDocument; }
            }

            var files = await vfs.ListFilesAsync().ConfigureAwait(false);
            if (files.FirstOrDefault(f => isVideo ? IsVideoExtension(f.Name) : IsImageExtension(f.Name)) is { } candidate)
            {
                return candidate.VfsPath;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            onRecoverableError?.Invoke(error, "Could not inspect Files VFS for studio input resolution.");
        }

        return currentDocument;
    }

    public static string SelectPreviewPngDataUrl(string? cmdId, bool isNewGradient = false)
    {
        if (isNewGradient || string.Equals(cmdId, "layer.newFillLayer.gradient", StringComparison.OrdinalIgnoreCase))
        {
            return GradientPreviewPngDataUrl;
        }

        return cmdId switch
        {
            "layer.removeBackground" => CutoutPreviewPngDataUrl,
            "image.adjustments.desaturate" => MonochromePreviewPngDataUrl,
            "preset.warm" => WarmPresetPngDataUrl,
            "preset.vivid" => VividPresetPngDataUrl,
            _ => CyanPreviewPngDataUrl,
        };
    }

    private static byte[] DecodeDataUrlBytes(string? dataUrl, string fallbackText)
    {
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            var markerIdx = dataUrl.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (markerIdx >= 0)
            {
                var base64 = dataUrl[(markerIdx + ";base64,".Length)..].Trim();
                var buffer = new byte[(base64.Length * 3 + 3) / 4];
                if (Convert.TryFromBase64String(base64, buffer, out var written)) { return buffer[..written]; }
            }
        }
        return Encoding.UTF8.GetBytes(fallbackText);
    }

    public static string? ExtractFileName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        foreach (var rawToken in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.Trim('"', '\'', ',', ';', '(', ')', '[', ']', '`', '@');
            if (IsImageExtension(token) || IsVideoExtension(token) || ArtCraftCatalog.FindByExtension(token) is not null)
            {
                if (token.StartsWith("neuron://", StringComparison.OrdinalIgnoreCase))
                {
                    var lastSlash = token.LastIndexOf('/');
                    return lastSlash >= 0 && lastSlash + 1 < token.Length ? token[(lastSlash + 1)..] : token;
                }
                return token;
            }
        }
        return null;
    }

    public static bool IsImageExtension(string pathOrName)
        => HasAnyExtension(pathOrName, ".png", ".jpg", ".jpeg", ".webp", ".psd");

    public static bool IsVideoExtension(string pathOrName)
        => HasAnyExtension(pathOrName, ".mp4", ".mov", ".fcproj");

    private static bool HasAnyExtension(string pathOrName, params string[] extensions)
        => extensions.Any(ext => pathOrName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static string EnsureWorkPath(string path) => path.StartsWith('/') ? path : "/work/" + path;

    private static string NonEmptyStem(string path, string fallback)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(stem) ? fallback : stem;
    }

    private static string DefaultJson(string? explicitJson, string fallback)
        => string.IsNullOrWhiteSpace(explicitJson) ? fallback : explicitJson!;

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string BuildStudioPreviewPng(string palette)
    {
        const int width = 240;
        const int height = 144;
        var raw = new byte[height * (1 + width * 4)];
        var offset = 0;

        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0; // filter: None
            var v = y / (float)(height - 1);
            for (var x = 0; x < width; x++)
            {
                var u = x / (float)(width - 1);
                var dx = (x - width * 0.5f) / (width * 0.24f);
                var dy = (y - height * 0.46f) / (height * 0.30f);
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                var inSubject = dist <= 1.0f;

                (byte r, byte g, byte b, byte a) = palette switch
                {
                    "cutout" => inSubject
                        ? ((byte)(36 + (int)(170 * (1f - dist))), (byte)(172 + (int)(65 * u)), (byte)(226 - (int)(45 * v)), (byte)255)
                        : ((byte)0, (byte)0, (byte)0, (byte)0),
                    "gradient" => (
                        (byte)(37 + (int)(212 * u)),
                        (byte)(99 + (int)(45 * (1f - v))),
                        (byte)(235 - (int)(210 * u)),
                        (byte)255),
                    "mono" => inSubject
                        ? ((byte)(212 - (int)(84 * dist)), (byte)(212 - (int)(84 * dist)), (byte)(216 - (int)(84 * dist)), (byte)255)
                        : ((byte)(36 + (int)(40 * (1f - v))), (byte)(36 + (int)(40 * (1f - v))), (byte)(40 + (int)(40 * (1f - v))), (byte)255),
                    "warm" => inSubject
                        ? ((byte)(242 - (int)(50 * dist)), (byte)(164 - (int)(48 * dist)), (byte)(92 - (int)(30 * dist)), (byte)255)
                        : ((byte)(58 + (int)(80 * (1f - v))), (byte)(38 + (int)(44 * u)), (byte)(28 + (int)(24 * (1f - u))), (byte)255),
                    "vivid" => inSubject
                        ? ((byte)(132 + (int)(110 * u)), (byte)(64 + (int)(168 * (1f - dist))), (byte)(246 - (int)(60 * v)), (byte)255)
                        : ((byte)(18 + (int)(54 * u)), (byte)(30 + (int)(80 * (1f - v))), (byte)(78 + (int)(92 * u)), (byte)255),
                    _ => inSubject
                        ? ((byte)(34 + (int)(120 * (1f - dist))), (byte)(186 + (int)(52 * u)), (byte)(228 - (int)(36 * v)), (byte)255)
                        : ((byte)(18 + (int)(28 * u)), (byte)(32 + (int)(48 * (1f - v))), (byte)(56 + (int)(64 * u)), (byte)255),
                };

                raw[offset++] = r;
                raw[offset++] = g;
                raw[offset++] = b;
                raw[offset++] = a;
            }
        }

        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]); // PNG magic

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr[..4], width);
        WriteBigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;  // 8-bit depth
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filter
        ihdr[12] = 0; // non-interlaced
        WritePngChunk(ms, "IHDR"u8, ihdr);

        using (var idatPayload = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idatPayload, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }
            WritePngChunk(ms, "IDAT"u8, idatPayload.GetBuffer().AsSpan(0, (int)idatPayload.Length));
        }

        WritePngChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return "data:image/png;base64," + Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[4];
        WriteBigEndian(header, data.Length);
        output.Write(header);
        output.Write(type);
        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        var crc = ComputeCrc32(type, data);
        WriteBigEndian(header, (int)crc);
        output.Write(header);
    }

    private static void WriteBigEndian(Span<byte> dest, int value)
    {
        dest[0] = (byte)((value >> 24) & 0xFF);
        dest[1] = (byte)((value >> 16) & 0xFF);
        dest[2] = (byte)((value >> 8) & 0xFF);
        dest[3] = (byte)(value & 0xFF);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc32(crc, type);
        crc = UpdateCrc32(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc;
    }
}
