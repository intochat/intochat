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
        const int width = 320;
        const int height = 180;
        var raw = new byte[height * (1 + width * 4)];
        var offset = 0;

        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0; // filter: None
            var v = y / (float)(height - 1);

            for (var x = 0; x < width; x++)
            {
                var u = x / (float)(width - 1);

                // Smooth cinema landscape ridge + architectural focal prism with anti-aliased SDF
                var ridgeA = 0.63f - 0.11f * MathF.Sin(u * 4.2f + 0.55f) - 0.05f * MathF.Cos(u * 9.5f - 0.8f);
                var ridgeB = 0.72f - 0.07f * MathF.Cos(u * 5.6f + 1.4f);

                // Sun / aperture key light disc (smooth anti-aliased glow)
                var sx = (u - 0.68f) * (width / (float)height);
                var sy = v - 0.34f;
                var sunDist = MathF.Sqrt(sx * sx + sy * sy);
                var sunDisc = Math.Clamp((0.145f - sunDist) * 45f, 0f, 1f);
                var sunGlow = MathF.Exp(-sunDist * 5.4f);

                // Architectural subject silhouette / portrait monolith in foreground
                var px = MathF.Abs(u - 0.36f);
                var py = MathF.Abs(v - 0.54f);
                var boxSdf = MathF.Max(px - 0.095f, py - 0.21f);
                var subjectMask = Math.Clamp((0.008f - boxSdf) * 90f, 0f, 1f);
                var rimMask = Math.Clamp((0.012f - MathF.Abs(boxSdf)) * 75f, 0f, 1f);

                // Subtle 1px studio rule-of-thirds & frame border
                var isFrame = x == 0 || x == width - 1 || y == 0 || y == height - 1;
                var isGuide = (x == width / 3 || x == width * 2 / 3 || y == height / 3 || y == height * 2 / 3)
                              && (x + y) % 4 < 2;

                float fr, fg, fb, fa = 1f;
                switch (palette)
                {
                    case "cutout":
                        {
                            var fgAlpha = MathF.Max(subjectMask, sunDisc * 0.92f);
                            if (fgAlpha <= 0.01f)
                            {
                                // Subtle dark studio alpha grid instead of glaring high-contrast grey blocks
                                var cell = (x / 10 + y / 10) % 2 == 0;
                                fr = cell ? 24f : 31f;
                                fg = cell ? 26f : 33f;
                                fb = cell ? 31f : 39f;
                                fa = 0.92f;
                            }
                            else
                            {
                                fr = 28f + 175f * sunGlow + 54f * rimMask;
                                fg = 152f + 76f * (1f - v) + 42f * rimMask;
                                fb = 214f + 32f * u;
                                fa = 1f;
                            }
                            break;
                        }
                    case "gradient":
                        {
                            fr = 24f + 188f * u + 28f * sunGlow;
                            fg = 48f + 92f * (1f - v) + 44f * u;
                            fb = 132f + 104f * (1f - u) * (0.65f + 0.35f * (1f - v));
                            break;
                        }
                    case "mono":
                        {
                            var luma = 26f + 110f * (1f - v) * 0.55f + 118f * sunGlow;
                            if (v > ridgeA) { luma *= 0.52f; }
                            if (v > ridgeB) { luma *= 0.68f; }
                            luma = luma * (1f - subjectMask) + (48f + 95f * (1f - v)) * subjectMask + 75f * rimMask;
                            fr = luma;
                            fg = luma;
                            fb = luma + 4f;
                            break;
                        }
                    case "warm":
                        {
                            fr = 34f + 168f * sunGlow + 56f * (1f - v);
                            fg = 24f + 104f * sunGlow + 28f * (1f - v);
                            fb = 28f + 42f * (1f - v) + 32f * sunGlow;
                            if (v > ridgeA) { fr *= 0.48f; fg *= 0.44f; fb *= 0.54f; }
                            if (v > ridgeB) { fr *= 0.68f; fg *= 0.65f; fb *= 0.72f; }
                            fr = fr * (1f - subjectMask) + 38f * subjectMask + 125f * rimMask;
                            fg = fg * (1f - subjectMask) + 28f * subjectMask + 82f * rimMask;
                            fb = fb * (1f - subjectMask) + 30f * subjectMask + 46f * rimMask;
                            break;
                        }
                    case "vivid":
                        {
                            fr = 28f + 164f * sunGlow + 78f * u;
                            fg = 22f + 86f * (1f - v) + 72f * sunGlow;
                            fb = 68f + 142f * (1f - u * 0.4f) * (1f - v * 0.45f);
                            if (v > ridgeA) { fr *= 0.46f; fg *= 0.48f; fb *= 0.58f; }
                            fr = fr * (1f - subjectMask) + 42f * subjectMask + 112f * rimMask;
                            fg = fg * (1f - subjectMask) + 36f * subjectMask + 148f * rimMask;
                            fb = fb * (1f - subjectMask) + 74f * subjectMask + 164f * rimMask;
                            break;
                        }
                    default:
                        {
                            fr = 18f + 64f * sunGlow + 24f * u;
                            fg = 34f + 122f * sunGlow + 52f * (1f - v);
                            fb = 58f + 146f * sunGlow + 68f * (1f - v * 0.5f);
                            if (v > ridgeA) { fr *= 0.50f; fg *= 0.52f; fb *= 0.58f; }
                            if (v > ridgeB) { fr *= 0.70f; fg *= 0.72f; fb *= 0.76f; }
                            fr = fr * (1f - subjectMask) + 24f * subjectMask + 70f * rimMask;
                            fg = fg * (1f - subjectMask) + 44f * subjectMask + 148f * rimMask;
                            fb = fb * (1f - subjectMask) + 66f * subjectMask + 176f * rimMask;
                            break;
                        }
                }

                if (sunDisc > 0.01f && palette != "gradient")
                {
                    var discWeight = sunDisc * 0.55f;
                    fr = fr * (1f - discWeight) + 248f * discWeight;
                    fg = fg * (1f - discWeight) + (palette == "warm" ? 204f : 238f) * discWeight;
                    fb = fb * (1f - discWeight) + (palette == "warm" ? 142f : 246f) * discWeight;
                }

                if (isGuide)
                {
                    fr = fr * 0.88f + 255f * 0.12f;
                    fg = fg * 0.88f + 255f * 0.12f;
                    fb = fb * 0.88f + 255f * 0.12f;
                }

                if (isFrame)
                {
                    fr = 54f;
                    fg = 58f;
                    fb = 66f;
                    fa = 1f;
                }

                raw[offset++] = (byte)Math.Clamp((int)fr, 0, 255);
                raw[offset++] = (byte)Math.Clamp((int)fg, 0, 255);
                raw[offset++] = (byte)Math.Clamp((int)fb, 0, 255);
                raw[offset++] = (byte)Math.Clamp((int)(fa * 255f), 0, 255);
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
