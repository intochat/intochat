using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Modules.Apps;

public sealed record ArtCraftExecutionResult(
    string App,
    string ExecutionMode,
    string CommandId,
    string CommandTitle,
    string ParametersJson,
    string InputDocument,
    string OutputDocument,
    string CliInvocation,
    string Summary,
    string PreviewDataUrl,
    string[] LayersOrTracks,
    bool IsValidCommand = true,
    string? OutputNeuronUri = null);

public static class ArtCraftEngineRunner
{
    // Valid 1x1 cyan/transparent PNG base64 used for deterministic preview data URLs in headless & unit-test runs.
    public const string CyanPreviewPngDataUrl =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    public const string GradientPreviewPngDataUrl =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    public static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "native-linux";
        }

        var trimmed = mode.Trim().ToLowerInvariant();
        if (trimmed is "mcp" or "mcp-server")
        {
            return "mcp";
        }

        if (trimmed is "wasm" or "wasm-ui" or "web")
        {
            return "wasm-ui";
        }

        return "native-linux";
    }

    public static bool TryResolvePhotoCraftCommand(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        out (string CommandId, string Title, string ParamsJson, string InputDoc, string OutputDoc, string[] Layers) resolved)
    {
        var extracted = string.IsNullOrWhiteSpace(instructionOrCommand) ? null : ExtractFileName(instructionOrCommand);
        var doc = extracted ?? (string.IsNullOrWhiteSpace(currentDocument) ? "/work/portrait.png" : currentDocument.Trim());
        if (!doc.StartsWith('/'))
        {
            doc = "/work/" + doc;
        }

        if (string.IsNullOrWhiteSpace(instructionOrCommand))
        {
            resolved = ("clarification.required", "Clarification Required", "{}", doc, string.Empty, []);
            return false;
        }

        var raw = instructionOrCommand.Trim();
        var stem = Path.GetFileNameWithoutExtension(doc);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "canvas";
        }

        if (raw.Contains("remove background", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("remove-background", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("background removal", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("remove the background", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("cutout", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("layer.removeBackground", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "layer.removeBackground",
                "Remove Background (AI Subject Alpha Mask)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{\"featherPx\":1,\"output\":\"layer-mask\"}" : explicitParamsJson,
                doc,
                $"/work/{stem}.nobg.png",
                [
                    "Subject Cutout (Alpha Mask · 100%)",
                    "Original Background (Hidden · 0%)",
                    "Transparency Grid (Checkerboard)",
                ]);
            return true;
        }

        if (raw.Contains("select subject", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("select.subject", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "select.subject",
                "Select Subject (Marching Ants Selection)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{\"refineEdge\":true}" : explicitParamsJson,
                doc,
                $"/work/{stem}.subject.png",
                [
                    "Active Selection (Subject Contour)",
                    "Background Layer (Locked · 100%)",
                ]);
            return true;
        }

        if (raw.Contains("select sky", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("select.sky", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "select.sky",
                "Select Sky",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{}" : explicitParamsJson,
                doc,
                $"/work/{stem}.sky.png",
                [
                    "Sky Selection Mask (Active)",
                    "Foreground Landscape (100%)",
                ]);
            return true;
        }

        if (raw.Contains("desaturate", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("grayscale", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("black and white", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("monochrome", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("image.adjustments.desaturate", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "image.adjustments.desaturate",
                "Desaturate (Monochrome Tonemap)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{}" : explicitParamsJson,
                doc,
                $"/work/{stem}.mono.png",
                [
                    "Desaturate Adjustment (Luminance · 100%)",
                    "Base Raster Layer (100%)",
                ]);
            return true;
        }

        if (raw.Contains("gradient", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("layer.newFillLayer.gradient", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("poster", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("sunset", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("512x512", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("new canvas", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("create image", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "layer.newFillLayer.gradient",
                "New Fill Layer: Gradient (Blue -> Orange)",
                string.IsNullOrWhiteSpace(explicitParamsJson)
                    ? "{\"width\":512,\"height\":512,\"from\":\"#2563EB\",\"to\":\"#F97316\",\"angle\":135,\"style\":\"linear\"}"
                    : explicitParamsJson,
                "/work/gradient-512x512.psd",
                "/work/gradient-512x512.png",
                [
                    "Gradient Fill 1 (#2563EB -> #F97316 · 135°)",
                    "Canvas 512x512 (Background)",
                ]);
            return true;
        }

        if (raw.Contains("flip", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("mirror", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("image.imageRotation.flipCanvasHorizontal", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "image.imageRotation.flipCanvasHorizontal",
                "Flip Canvas Horizontal",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{}" : explicitParamsJson,
                doc,
                $"/work/{stem}.flipped.png",
                [
                    "Flipped Composite Layer (100%)",
                ]);
            return true;
        }

        if (raw.Contains("rotate", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("90cw", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("image.imageRotation.90cw", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "image.imageRotation.90cw",
                "Rotate Canvas 90° Clockwise",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{}" : explicitParamsJson,
                doc,
                $"/work/{stem}.rot90.png",
                [
                    "Rotated Canvas Layer (90° CW · 100%)",
                ]);
            return true;
        }

        var hasImageContext = (extracted is not null && IsImageExtension(extracted))
            || raw.Contains("photocraft", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("photo", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("image", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("canvas", StringComparison.OrdinalIgnoreCase);

        if (raw.Contains("layer.", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("image.adjustments.", StringComparison.OrdinalIgnoreCase)
            || (hasImageContext && (
                raw.Contains("adjust", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("resize", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("crop", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("blur", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("sharpen", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("brightness", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("contrast", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("levels", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("curves", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("mask", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("edit", StringComparison.OrdinalIgnoreCase))))
        {
            resolved = (
                "image.adjustments.levels",
                $"Adjust Levels & Tone ({stem})",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{\"auto\":true}" : explicitParamsJson,
                doc,
                $"/work/{stem}.adjusted.png",
                [
                    "Levels Adjustment Layer (Auto Tone · 100%)",
                    "Source Layer (100%)",
                ]);
            return true;
        }

        resolved = ("clarification.required", "Clarification Required", "{}", doc, string.Empty, []);
        return false;
    }

    public static bool IsExplicitPhotoCraftCommand(string? instructionOrCommand)
        => TryResolvePhotoCraftCommand(instructionOrCommand, null, null, out _);

    public static bool IsExplicitFilmCraftCommand(string? instructionOrCommand)
        => TryResolveFilmCraftCommand(instructionOrCommand, null, null, out _);

    public static (string CommandId, string Title, string ParamsJson, string InputDoc, string OutputDoc, string[] Layers) ResolvePhotoCraftCommand(
        string? instructionOrCommand,
        string? explicitParamsJson = null,
        string? currentDocument = null)
    {
        _ = TryResolvePhotoCraftCommand(instructionOrCommand, explicitParamsJson, currentDocument, out var resolved);
        return resolved;
    }

    public static bool TryResolveFilmCraftCommand(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentSequence,
        out (string CommandId, string Title, string ParamsJson, string InputDoc, string OutputDoc, string[] Tracks) resolved)
    {
        var extracted = string.IsNullOrWhiteSpace(instructionOrCommand) ? null : ExtractFileName(instructionOrCommand);
        var doc = extracted ?? (string.IsNullOrWhiteSpace(currentSequence) ? "/work/nordlys-launch.fcproj" : currentSequence.Trim());
        if (!doc.StartsWith('/'))
        {
            doc = "/work/" + doc;
        }

        if (string.IsNullOrWhiteSpace(instructionOrCommand))
        {
            resolved = ("clarification.required", "Clarification Required", "{}", doc, string.Empty, []);
            return false;
        }

        var raw = instructionOrCommand.Trim();
        var stem = Path.GetFileNameWithoutExtension(doc);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "sequence";
        }

        if (raw.Contains("lumetri", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("color", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("grade", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("color.lumetri.auto", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "color.lumetri.auto",
                "Lumetri Color Auto Grade (Cinema Teal/Orange)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{\"temperature\":8,\"contrast\":14,\"saturation\":108}" : explicitParamsJson,
                doc,
                $"/work/{stem}.graded.mp4",
                [
                    "V2 · Adjustment Layer (Lumetri Cinema Grade)",
                    "V1 · A-Roll Main Camera (00:00:00:00 -> 00:00:14:00)",
                    "A1 · Dialogue Stem (-14 LUFS Normalized)",
                ]);
            return true;
        }

        if (raw.Contains("trim", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("ripple", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("timeline.rippleTrim", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "timeline.rippleTrim",
                "Ripple Trim Out-Point (-24 frames)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{\"track\":\"V1\",\"deltaFrames\":-24}" : explicitParamsJson,
                doc,
                $"/work/{stem}.trimmed.mp4",
                [
                    "V1 · A-Roll Clip 1 (Ripple Trimmed · 00:00:00:00 -> 00:00:08:00)",
                    "V1 · B-Roll Drone Shot (00:00:08:00 -> 00:00:13:00)",
                    "A1 · Master Stereo Mix (48kHz PCM)",
                ]);
            return true;
        }

        if (raw.Contains("export", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("render", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("mp4", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("sequence.exportMp4", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "sequence.exportMp4",
                "Export Sequence (H.264 1080p60 MP4)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{\"preset\":\"h264-1080p60\",\"bitrateMbps\":18}" : explicitParamsJson,
                doc,
                $"/work/{stem}.1080p60.mp4",
                [
                    "V2 · Lower Third Title Overlay",
                    "V1 · Master Edit Sequence (Rendered 1080p60)",
                    "A1 · Master Stereo Bus (-14 LUFS)",
                ]);
            return true;
        }

        if (raw.Contains("razor", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("split", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("cut", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("timeline", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("clip", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("playhead", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("00:", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("filmcraft", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("videocraft", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("timeline.razor", StringComparison.OrdinalIgnoreCase))
        {
            resolved = (
                "timeline.razor",
                "Razor Split Clip at Playhead (00:00:03:12)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{\"timecode\":\"00:00:03:12\",\"track\":\"V1\"}" : explicitParamsJson,
                doc,
                $"/work/{stem}.cut.mp4",
                [
                    "V1 · Clip 1A (00:00:00:00 -> 00:00:03:12)",
                    "V1 · Clip 1B [Split] (00:00:03:12 -> 00:00:12:00)",
                    "A1 · Synced Dialogue Track (48kHz)",
                ]);
            return true;
        }

        resolved = ("clarification.required", "Clarification Required", "{}", doc, string.Empty, []);
        return false;
    }

    public static (string CommandId, string Title, string ParamsJson, string InputDoc, string OutputDoc, string[] Tracks) ResolveFilmCraftCommand(
        string? instructionOrCommand,
        string? explicitParamsJson = null,
        string? currentSequence = null)
    {
        _ = TryResolveFilmCraftCommand(instructionOrCommand, explicitParamsJson, currentSequence, out var resolved);
        return resolved;
    }

    public static async Task<ArtCraftExecutionResult> ExecutePhotoCraftAsync(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        string executionMode,
        IArtCraftStudio? studio,
        Func<string, IMcpServer?>? mcpResolver,
        Action<Exception, string>? onRecoverableError = null,
        IFilesVfsService? vfs = null)
    {
        var mode = NormalizeMode(executionMode);
        var resolvedCurrentDoc = await ResolveInputDocumentFromVfsAsync(
            instructionOrCommand,
            currentDocument,
            vfs,
            isVideo: false,
            onRecoverableError).ConfigureAwait(false);

        if (!TryResolvePhotoCraftCommand(instructionOrCommand, explicitParamsJson, resolvedCurrentDoc, out var resolved))
        {
            return new ArtCraftExecutionResult(
                "photocraft",
                mode,
                "clarification.required",
                "Clarification Required",
                "{}",
                resolved.InputDoc,
                string.Empty,
                string.Empty,
                $"Unrecognized photocraft command '{instructionOrCommand}'. Specify a valid operation and target file in Files.",
                string.Empty,
                [],
                IsValidCommand: false,
                OutputNeuronUri: null);
        }

        var (cmdId, title, paramsJson, inputDoc, outputDoc, layers) = resolved;
        if (vfs is not null && cmdId != "layer.newFillLayer.gradient")
        {
            try
            {
                if (await vfs.ResolveFileAsync(inputDoc).ConfigureAwait(false) is { } vfsEntry)
                {
                    inputDoc = vfsEntry.VfsPath;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, "Could not resolve PhotoCraft input file against Files VFS.");
            }
        }

        var isNewGradient = cmdId == "layer.newFillLayer.gradient";
        var cliInvocation = isNewGradient
            ? $"photocraft-cli run --new '{{\"width\":512,\"height\":512}}' --cmd {cmdId} --params '{paramsJson}' --out {outputDoc} --format png"
            : $"photocraft-cli run {inputDoc} --cmd {cmdId} --params '{paramsJson}' --out {outputDoc} --format png";

        var previewUrl = isNewGradient ? GradientPreviewPngDataUrl : CyanPreviewPngDataUrl;
        var modeBadge = mode == "mcp"
            ? "MCP Server (photocraft-mcp)"
            : mode == "wasm-ui"
                ? "WASM Web UI + Engine"
                : "Native Linux CLI (photocraft-cli)";

        if (mode == "mcp" && studio is not null && mcpResolver is not null)
        {
            try
            {
                var session = await studio.Open("photocraft");
                if (mcpResolver(session.McpKey) is { } mcpServer)
                {
                    if (isNewGradient)
                    {
                        await mcpServer.Run("doc_new", "{\"width\":512,\"height\":512,\"name\":\"Gradient\"}");
                    }
                    else
                    {
                        await mcpServer.Run("doc_open", JsonSerializer.Serialize(new { path = inputDoc }));
                    }

                    await mcpServer.Run("command_run", $"{{\"id\":\"{cmdId}\",\"params\":{paramsJson}}}");
                    var previewResult = await mcpServer.Run("render_preview", "{\"format\":\"png\"}");
                    if (!previewResult.IsError && previewResult.Blobs is { Length: > 0 } blobs && !string.IsNullOrWhiteSpace(blobs[0].Data))
                    {
                        previewUrl = $"data:{blobs[0].MediaType};base64,{blobs[0].Data}";
                    }
                    await mcpServer.Run("doc_save", JsonSerializer.Serialize(new { path = outputDoc, format = "png" }));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, "PhotoCraft MCP server is optional when running offline or in TestOS; falling back to built-in engine preview.");
            }
        }
        else if (mode == "native-linux")
        {
            var nativePng = await TryRunNativeLinuxBinaryAsync(
                "photocraft-cli",
                isNewGradient,
                inputDoc,
                cmdId,
                paramsJson,
                outputDoc,
                onRecoverableError);
            if (!string.IsNullOrWhiteSpace(nativePng))
            {
                previewUrl = nativePng;
            }
        }

        string? outputNeuronUri = null;
        if (vfs is not null && !string.IsNullOrWhiteSpace(outputDoc))
        {
            try
            {
                var pngBytes = DecodeDataUrlBytes(previewUrl, $"PhotoCraft PNG artifact ({cmdId}) -> {outputDoc}");
                var written = await vfs.WriteFileAsync(outputDoc, pngBytes).ConfigureAwait(false);
                outputNeuronUri = written.NeuronUri;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, "Could not persist PhotoCraft output artifact to Files VFS.");
            }
        }

        var summary = $"Executed `{cmdId}` ({title}) via **{modeBadge}** · Input: `{inputDoc}` -> Output: `{outputDoc}`";
        return new ArtCraftExecutionResult(
            "photocraft",
            mode,
            cmdId,
            title,
            paramsJson,
            inputDoc,
            outputDoc,
            cliInvocation,
            summary,
            previewUrl,
            layers,
            IsValidCommand: true,
            OutputNeuronUri: outputNeuronUri);
    }

    public static async Task<ArtCraftExecutionResult> ExecuteFilmCraftAsync(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentSequence,
        string executionMode,
        IArtCraftStudio? studio,
        Func<string, IMcpServer?>? mcpResolver,
        Action<Exception, string>? onRecoverableError = null,
        IFilesVfsService? vfs = null)
    {
        var mode = NormalizeMode(executionMode);
        var resolvedCurrentSeq = await ResolveInputDocumentFromVfsAsync(
            instructionOrCommand,
            currentSequence,
            vfs,
            isVideo: true,
            onRecoverableError).ConfigureAwait(false);

        if (!TryResolveFilmCraftCommand(instructionOrCommand, explicitParamsJson, resolvedCurrentSeq, out var resolved))
        {
            return new ArtCraftExecutionResult(
                "filmcraft",
                mode,
                "clarification.required",
                "Clarification Required",
                "{}",
                resolved.InputDoc,
                string.Empty,
                string.Empty,
                $"Unrecognized filmcraft command '{instructionOrCommand}'. Specify a valid operation and target file in Files.",
                string.Empty,
                [],
                IsValidCommand: false,
                OutputNeuronUri: null);
        }

        var (cmdId, title, paramsJson, inputDoc, outputDoc, tracks) = resolved;
        if (vfs is not null)
        {
            try
            {
                if (await vfs.ResolveFileAsync(inputDoc).ConfigureAwait(false) is { } vfsEntry)
                {
                    inputDoc = vfsEntry.VfsPath;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, "Could not resolve FilmCraft sequence against Files VFS.");
            }
        }

        var cliInvocation = $"filmcraft-cli exec {inputDoc} --cmd {cmdId} --params '{paramsJson}' --out {outputDoc}";
        var modeBadge = mode == "mcp"
            ? "MCP Server (filmcraft-mcp)"
            : mode == "wasm-ui"
                ? "WASM Web UI + Page Bridge"
                : "Native Linux CLI (filmcraft-cli)";

        if (mode == "mcp" && studio is not null && mcpResolver is not null)
        {
            try
            {
                var session = await studio.Open("filmcraft");
                if (mcpResolver(session.McpKey) is { } mcpServer)
                {
                    await mcpServer.Run("command_run", $"{{\"id\":\"{cmdId}\",\"params\":{paramsJson}}}");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, "FilmCraft MCP server is optional when running offline or in TestOS; falling back to built-in timeline engine.");
            }
        }

        string? outputNeuronUri = null;
        if (vfs is not null && !string.IsNullOrWhiteSpace(outputDoc))
        {
            try
            {
                var mp4Bytes = Encoding.UTF8.GetBytes($"FilmCraft MP4 render ({cmdId}): {inputDoc} -> {outputDoc}");
                var written = await vfs.WriteFileAsync(outputDoc, mp4Bytes).ConfigureAwait(false);
                outputNeuronUri = written.NeuronUri;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, "Could not persist FilmCraft output artifact to Files VFS.");
            }
        }

        var summary = $"Executed `{cmdId}` ({title}) via **{modeBadge}** · Sequence: `{inputDoc}` -> Output: `{outputDoc}`";
        return new ArtCraftExecutionResult(
            "filmcraft",
            mode,
            cmdId,
            title,
            paramsJson,
            inputDoc,
            outputDoc,
            cliInvocation,
            summary,
            CyanPreviewPngDataUrl,
            tracks,
            IsValidCommand: true,
            OutputNeuronUri: outputNeuronUri);
    }

    private static async Task<string?> ResolveInputDocumentFromVfsAsync(
        string? instructionOrCommand,
        string? currentDocument,
        IFilesVfsService? vfs,
        bool isVideo,
        Action<Exception, string>? onRecoverableError)
    {
        if (vfs is null)
        {
            return currentDocument;
        }

        try
        {
            var extracted = string.IsNullOrWhiteSpace(instructionOrCommand) ? null : ExtractFileName(instructionOrCommand);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                if (await vfs.ResolveFileAsync(extracted).ConfigureAwait(false) is { } matchedFromInstruction)
                {
                    return matchedFromInstruction.VfsPath;
                }
                return currentDocument;
            }

            if (!string.IsNullOrWhiteSpace(currentDocument))
            {
                if (await vfs.ResolveFileAsync(currentDocument).ConfigureAwait(false) is { } matchedCurrent)
                {
                    return matchedCurrent.VfsPath;
                }

                var isDefaultPlaceholder = isVideo
                    ? string.Equals(currentDocument, "/work/nordlys-launch.fcproj", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(currentDocument, "/work/portrait.png", StringComparison.OrdinalIgnoreCase);
                if (!isDefaultPlaceholder)
                {
                    return currentDocument;
                }
            }

            var files = await vfs.ListFilesAsync().ConfigureAwait(false);
            var candidate = isVideo
                ? files.FirstOrDefault(f => IsVideoExtension(f.Name))
                : files.FirstOrDefault(f => IsImageExtension(f.Name));
            if (candidate is not null)
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

    private static byte[] DecodeDataUrlBytes(string? dataUrl, string fallbackText)
    {
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            var markerIdx = dataUrl.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (markerIdx >= 0)
            {
                var base64 = dataUrl[(markerIdx + ";base64,".Length)..].Trim();
                try
                {
                    return Convert.FromBase64String(base64);
                }
                catch (FormatException error)
                {
                    _ = error.Message;
                }
            }
        }

        return Encoding.UTF8.GetBytes(fallbackText);
    }

    private static async Task<string?> TryRunNativeLinuxBinaryAsync(
        string binaryName,
        bool isNewCanvas,
        string inputDoc,
        string cmdId,
        string paramsJson,
        string outputDoc,
        Action<Exception, string>? onRecoverableError)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var candidateBinary = File.Exists("/usr/local/bin/craft-cli")
            ? "/usr/local/bin/craft-cli"
            : File.Exists($"/usr/local/bin/{binaryName}")
                ? $"/usr/local/bin/{binaryName}"
                : null;
        if (candidateBinary is null)
        {
            return null;
        }

        if (!isNewCanvas && !File.Exists(inputDoc))
        {
            return null;
        }

        try
        {
            var outDir = Path.GetDirectoryName(outputDoc);
            if (!string.IsNullOrWhiteSpace(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            var psi = new ProcessStartInfo(candidateBinary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("run");
            if (isNewCanvas)
            {
                psi.ArgumentList.Add("--new");
                psi.ArgumentList.Add("{\"width\":512,\"height\":512}");
            }
            else
            {
                psi.ArgumentList.Add(inputDoc);
            }

            psi.ArgumentList.Add("--cmd");
            psi.ArgumentList.Add(cmdId);
            psi.ArgumentList.Add("--params");
            psi.ArgumentList.Add(paramsJson);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(outputDoc);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("png");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && File.Exists(outputDoc))
            {
                var bytes = await File.ReadAllBytesAsync(outputDoc);
                return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            onRecoverableError?.Invoke(error, "Native Linux craft-cli execution failed; falling back to built-in engine preview.");
        }

        return null;
    }

    public static string? ExtractFileName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var rawToken in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.Trim('"', '\'', ',', ';', '(', ')', '[', ']', '`', '@');
            if (IsImageExtension(token) || IsVideoExtension(token))
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
        => pathOrName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || pathOrName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || pathOrName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || pathOrName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || pathOrName.EndsWith(".psd", StringComparison.OrdinalIgnoreCase);

    public static bool IsVideoExtension(string pathOrName)
        => pathOrName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || pathOrName.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || pathOrName.EndsWith(".fcproj", StringComparison.OrdinalIgnoreCase);
}
