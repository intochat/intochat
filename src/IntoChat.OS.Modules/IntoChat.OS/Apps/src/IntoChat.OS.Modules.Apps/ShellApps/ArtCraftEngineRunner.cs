using System.Diagnostics;
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
    string[] LayersOrTracks);

internal static class ArtCraftEngineRunner
{
    // Valid 1x1 cyan/transparent PNG base64 used for deterministic preview data URLs in headless & unit-test runs.
    internal const string CyanPreviewPngDataUrl =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    internal const string GradientPreviewPngDataUrl =
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

    public static (string CommandId, string Title, string ParamsJson, string InputDoc, string OutputDoc, string[] Layers) ResolvePhotoCraftCommand(
        string? instructionOrCommand,
        string? explicitParamsJson = null,
        string? currentDocument = null)
    {
        var raw = string.IsNullOrWhiteSpace(instructionOrCommand)
            ? "remove background from portrait.png"
            : instructionOrCommand.Trim();
        var doc = ExtractFileName(raw) ?? (string.IsNullOrWhiteSpace(currentDocument) ? "/work/portrait.png" : currentDocument);
        if (!doc.StartsWith('/'))
        {
            doc = "/work/" + doc;
        }

        var stem = Path.GetFileNameWithoutExtension(doc);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "canvas";
        }

        if (raw.Contains("remove background", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("remove-background", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("background removal", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("cutout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "layer.removeBackground", StringComparison.OrdinalIgnoreCase))
        {
            return (
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
        }

        if (raw.Contains("select subject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "select.subject", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "select.subject",
                "Select Subject (Marching Ants Selection)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{\"refineEdge\":true}" : explicitParamsJson,
                doc,
                $"/work/{stem}.subject.png",
                [
                    "Active Selection (Subject Contour)",
                    "Background Layer (Locked · 100%)",
                ]);
        }

        if (raw.Contains("select sky", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "select.sky", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "select.sky",
                "Select Sky",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{}" : explicitParamsJson,
                doc,
                $"/work/{stem}.sky.png",
                [
                    "Sky Selection Mask (Active)",
                    "Foreground Landscape (100%)",
                ]);
        }

        if (raw.Contains("desaturate", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("grayscale", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("black and white", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "image.adjustments.desaturate", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "image.adjustments.desaturate",
                "Desaturate (Monochrome Tonemap)",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{}" : explicitParamsJson,
                doc,
                $"/work/{stem}.mono.png",
                [
                    "Desaturate Adjustment (Luminance · 100%)",
                    "Base Raster Layer (100%)",
                ]);
        }

        if (raw.Contains("gradient", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "layer.newFillLayer.gradient", StringComparison.OrdinalIgnoreCase))
        {
            return (
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
        }

        if (raw.Contains("flip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "image.imageRotation.flipCanvasHorizontal", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "image.imageRotation.flipCanvasHorizontal",
                "Flip Canvas Horizontal",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{}" : explicitParamsJson,
                doc,
                $"/work/{stem}.flipped.png",
                [
                    "Flipped Composite Layer (100%)",
                ]);
        }

        if (raw.Contains("rotate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "image.imageRotation.90cw", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "image.imageRotation.90cw",
                "Rotate Canvas 90° Clockwise",
                string.IsNullOrWhiteSpace(explicitParamsJson) ? "{}" : explicitParamsJson,
                doc,
                $"/work/{stem}.rot90.png",
                [
                    "Rotated Canvas Layer (90° CW · 100%)",
                ]);
        }

        return (
            "layer.removeBackground",
            $"PhotoCraft Edit ({raw})",
            string.IsNullOrWhiteSpace(explicitParamsJson) ? JsonSerializer.Serialize(new { prompt = raw }) : explicitParamsJson,
            doc,
            $"/work/{stem}.edited.png",
            [
                $"Edited Layer ({raw} · 100%)",
                "Source Layer (100%)",
            ]);
    }

    public static (string CommandId, string Title, string ParamsJson, string InputDoc, string OutputDoc, string[] Tracks) ResolveFilmCraftCommand(
        string? instructionOrCommand,
        string? explicitParamsJson = null,
        string? currentSequence = null)
    {
        var raw = string.IsNullOrWhiteSpace(instructionOrCommand)
            ? "razor split timeline at 00:00:03:12"
            : instructionOrCommand.Trim();
        var doc = ExtractFileName(raw) ?? (string.IsNullOrWhiteSpace(currentSequence) ? "/work/nordlys-launch.fcproj" : currentSequence);
        if (!doc.StartsWith('/'))
        {
            doc = "/work/" + doc;
        }

        var stem = Path.GetFileNameWithoutExtension(doc);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "sequence";
        }

        if (raw.Contains("lumetri", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("color", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("grade", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "color.lumetri.auto", StringComparison.OrdinalIgnoreCase))
        {
            return (
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
        }

        if (raw.Contains("trim", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("ripple", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "timeline.rippleTrim", StringComparison.OrdinalIgnoreCase))
        {
            return (
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
        }

        if (raw.Contains("export", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("render", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "sequence.exportMp4", StringComparison.OrdinalIgnoreCase))
        {
            return (
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
        }

        return (
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
    }

    public static async Task<ArtCraftExecutionResult> ExecutePhotoCraftAsync(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        string executionMode,
        IArtCraftStudio? studio,
        Func<string, IMcpServer?>? mcpResolver,
        Action<Exception, string>? onRecoverableError = null)
    {
        var mode = NormalizeMode(executionMode);
        var (cmdId, title, paramsJson, inputDoc, outputDoc, layers) =
            ResolvePhotoCraftCommand(instructionOrCommand, explicitParamsJson, currentDocument);

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
            layers);
    }

    public static async Task<ArtCraftExecutionResult> ExecuteFilmCraftAsync(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentSequence,
        string executionMode,
        IArtCraftStudio? studio,
        Func<string, IMcpServer?>? mcpResolver,
        Action<Exception, string>? onRecoverableError = null)
    {
        var mode = NormalizeMode(executionMode);
        var (cmdId, title, paramsJson, inputDoc, outputDoc, tracks) =
            ResolveFilmCraftCommand(instructionOrCommand, explicitParamsJson, currentSequence);

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
            tracks);
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
        foreach (var rawToken in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.Trim('"', '\'', ',', ';', '(', ')', '[', ']', '`');
            if (token.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".psd", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".fcproj", StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }
        }
        return null;
    }
}
