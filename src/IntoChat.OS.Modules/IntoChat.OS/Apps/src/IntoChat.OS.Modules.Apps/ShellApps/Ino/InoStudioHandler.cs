using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Modules.Apps;

internal static class InoStudioHandler
{
    public static bool TryMatchExplicitStudioIntent(string text, out string studioApp)
    {
        studioApp = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || InoCapabilityCatalog.IsCapabilityOrMetaInquiry(text))
        {
            return false;
        }

        if (IsPhotoCraftIntent(text))
        {
            studioApp = "photocraft";
            return true;
        }

        if (IsFilmCraftIntent(text))
        {
            studioApp = "filmcraft";
            return true;
        }

        return false;
    }

    public static bool IsPhotoCraftIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || InoCapabilityCatalog.IsCapabilityOrMetaInquiry(text))
        {
            return false;
        }

        if (text.Contains("photocraft", StringComparison.OrdinalIgnoreCase)
            || text.Contains("remove background", StringComparison.OrdinalIgnoreCase)
            || text.Contains("remove-background", StringComparison.OrdinalIgnoreCase)
            || text.Contains("background removal", StringComparison.OrdinalIgnoreCase)
            || text.Contains("select subject", StringComparison.OrdinalIgnoreCase)
            || text.Contains("select sky", StringComparison.OrdinalIgnoreCase)
            || text.Contains("desaturate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("layer.removeBackground", StringComparison.OrdinalIgnoreCase)
            || text.Contains("layer.newFillLayer.gradient", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var mentionsImage = text.Contains("photo", StringComparison.OrdinalIgnoreCase)
            || text.Contains("image", StringComparison.OrdinalIgnoreCase)
            || text.Contains("picture", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".png", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".webp", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".psd", StringComparison.OrdinalIgnoreCase)
            || text.Contains("512x512", StringComparison.OrdinalIgnoreCase);

        return mentionsImage && (
            text.Contains("gradient", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cutout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("grayscale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("flip", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rotate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("edit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("adjust", StringComparison.OrdinalIgnoreCase)
            || text.Contains("crop", StringComparison.OrdinalIgnoreCase)
            || text.Contains("resize", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsFilmCraftIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || InoCapabilityCatalog.IsCapabilityOrMetaInquiry(text))
        {
            return false;
        }

        return text.Contains("filmcraft", StringComparison.OrdinalIgnoreCase)
            || text.Contains("videocraft", StringComparison.OrdinalIgnoreCase)
            || text.Contains("razor split", StringComparison.OrdinalIgnoreCase)
            || text.Contains("split timeline", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cut timeline", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ripple trim", StringComparison.OrdinalIgnoreCase)
            || text.Contains("trim video", StringComparison.OrdinalIgnoreCase)
            || text.Contains("lumetri", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeline.razor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeline.rippleTrim", StringComparison.OrdinalIgnoreCase)
            || text.Contains("color.lumetri.auto", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sequence.exportMp4", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<InoAppState> ExecuteAsync(
        InoAppState currentState,
        string studioApp,
        string text,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        IMemory? memory,
        IKnowledgeStore? knowledgeStore,
        IFilesVfsService? filesVfs,
        IArtCraftStudio? artCraftStudio,
        Func<string, IMcpServer?>? mcpResolver,
        Func<string, IApp?> tryGetApp,
        Action<Exception, string>? onRecoverableError = null)
    {
        var requestedMode = text.Contains("mcp", StringComparison.OrdinalIgnoreCase)
            ? "mcp"
            : text.Contains("wasm", StringComparison.OrdinalIgnoreCase)
                ? "wasm-ui"
                : "native-linux";

        string? resolvedInputDoc = null;
        if (filesVfs is not null)
        {
            var tokenFile = ArtCraftEngineRunner.ExtractFileName(text);
            if (!string.IsNullOrWhiteSpace(tokenFile))
            {
                var entry = await filesVfs.ResolveFileAsync(tokenFile);
                if (entry is not null)
                {
                    resolvedInputDoc = entry.VfsPath;
                }
            }
        }

        var result = studioApp == "photocraft"
            ? await ArtCraftEngineRunner.ExecutePhotoCraftAsync(
                text,
                explicitParamsJson: null,
                currentDocument: resolvedInputDoc,
                requestedMode,
                artCraftStudio,
                mcpResolver,
                onRecoverableError,
                filesVfs)
            : await ArtCraftEngineRunner.ExecuteFilmCraftAsync(
                text,
                explicitParamsJson: null,
                currentSequence: resolvedInputDoc,
                requestedMode,
                artCraftStudio,
                mcpResolver,
                onRecoverableError,
                filesVfs);

        if (!result.IsValidCommand)
        {
            var validOps = studioApp == "photocraft"
                ? "`remove background`, `select subject`, `select sky`, `desaturate`, `gradient`, `flip`, or `rotate`"
                : "`razor split`, `ripple trim`, `lumetri color grade`, or `export mp4`";
            var clarificationReply =
                $"Ino ({settings.DefaultModel}): {result.Summary}\n\n"
                + $"- **Supported {studioApp} operations**: {validOps}";
            var clarificationRoute = $"clarification:{studioApp}";
            var clarificationTurns = currentState.Turns.Append(
                new InoTurn(text, clarificationReply, clarificationRoute, recalled.Length)).ToArray();

            return currentState with
            {
                Status = result.Summary,
                Turns = clarificationTurns,
            };
        }

        try
        {
            if (tryGetApp($"apps/{studioApp}") is { } targetStudioApp)
            {
                await targetStudioApp.Request("edit", text);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            onRecoverableError?.Invoke(error, $"{studioApp} app neuron is optional when running InoApp in standalone unit tests.");
        }

        var turnsCount = currentState.Turns.Length + 1;
        var cardId = $"studio-{turnsCount}";
        var sourceUri = !string.IsNullOrWhiteSpace(result.OutputNeuronUri)
            ? result.OutputNeuronUri
            : $"neuron://local/apps/{studioApp}";
        var studioCard = new InoCard(
            cardId,
            studioApp,
            $"{result.CommandTitle} [{result.ExecutionMode}]",
            $"{result.Summary} · CLI: {result.CliInvocation}",
            sourceUri,
            "completed",
            $"neuron://local/apps/ino#{cardId}");

        var routeHint = $"routed:{studioApp}.{result.CommandId}";
        var vfsLine = !string.IsNullOrWhiteSpace(result.OutputNeuronUri)
            ? $"\n- **Files Workspace URI**: `{result.OutputNeuronUri}`"
            : string.Empty;
        var reply =
            $"Ino ({settings.DefaultModel}): {result.Summary}\n\n"
            + $"- **Command**: `{result.CommandId}` ({result.CommandTitle})\n"
            + $"- **Execution Mode**: `{result.ExecutionMode}` (`{result.CliInvocation}`)\n"
            + $"- **Output Artifact**: `{result.OutputDocument}`{vfsLine}\n"
            + $"- **Layers / Tracks**: {string.Join(", ", result.LayersOrTracks)}";

        if (memory is not null)
        {
            await memory.CaptureEpisode($"ino.{studioApp}", $"User: {text} | Cmd: {result.CommandId} | Out: {result.OutputDocument}", importance: 7);
        }

        if (knowledgeStore is not null)
        {
            try
            {
                await knowledgeStore.RecordNodeAsync(new KnowledgeNode(
                    $"artifact:{result.OutputDocument}",
                    result.OutputDocument,
                    studioApp,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["command"] = result.CommandId,
                        ["input"] = result.InputDocument,
                        ["uri"] = sourceUri,
                    }));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }
        }

        var title = currentState.Turns.Length == 0
            ? (text.Length <= 52 ? text : text[..52])
            : currentState.Title;
        var nextTurns = currentState.Turns.Append(new InoTurn(text, reply, routeHint, recalled.Length)).ToArray();

        return currentState with
        {
            Title = title,
            Status = $"{result.CommandTitle} ({result.ExecutionMode}) -> {result.OutputDocument}",
            Turns = nextTurns,
            Cards = [.. currentState.ActiveCards, studioCard],
        };
    }
}
