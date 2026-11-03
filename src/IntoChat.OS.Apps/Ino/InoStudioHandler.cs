using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Apps.Ino;

internal static class InoStudioHandler
{
    public static bool TryMatchExplicitStudioIntent(string text, out string studioApp)
    {
        studioApp = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || InoCapabilityCatalog.IsCapabilityOrMetaInquiry(text))
        {
            return false;
        }

        if (IsMarkdrawIntent(text))
        {
            studioApp = "markdraw";
            return true;
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

    public static bool IsMarkdrawIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || InoCapabilityCatalog.IsCapabilityOrMetaInquiry(text))
        {
            return false;
        }

        if (text.StartsWith("create file ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("write file ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("save file ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("read ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("inspect ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Contains("markdraw", StringComparison.OrdinalIgnoreCase)
            || text.Contains("excalidraw", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".markdraw", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".excalidraw", StringComparison.OrdinalIgnoreCase)
            || text.Contains("co-draw", StringComparison.OrdinalIgnoreCase)
            || text.Contains("codraw", StringComparison.OrdinalIgnoreCase)
            || text.Contains("patch-elements", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var mentionsDiagramOrCanvas = text.Contains("diagram", StringComparison.OrdinalIgnoreCase)
            || text.Contains("flowchart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("whiteboard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("architecture canvas", StringComparison.OrdinalIgnoreCase)
            || text.Contains("on the canvas", StringComparison.OrdinalIgnoreCase);

        return mentionsDiagramOrCanvas && (
            text.Contains("draw", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sketch", StringComparison.OrdinalIgnoreCase)
            || text.Contains("add ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("connect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("node", StringComparison.OrdinalIgnoreCase)
            || text.Contains("arrow", StringComparison.OrdinalIgnoreCase)
            || text.Contains("create", StringComparison.OrdinalIgnoreCase)
            || text.Contains("open", StringComparison.OrdinalIgnoreCase)
            || text.Contains("export", StringComparison.OrdinalIgnoreCase));
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
        if (string.Equals(studioApp, "markdraw", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteMarkdrawAsync(
                currentState,
                text,
                settings,
                recalled,
                memory,
                knowledgeStore,
                filesVfs,
                tryGetApp,
                onRecoverableError);
        }

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

    private static async Task<InoAppState> ExecuteMarkdrawAsync(
        InoAppState currentState,
        string text,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        IMemory? memory,
        IKnowledgeStore? knowledgeStore,
        IFilesVfsService? filesVfs,
        Func<string, IApp?> tryGetApp,
        Action<Exception, string>? onRecoverableError)
    {
        var existingCard = currentState.ActiveCards.LastOrDefault(c => c.Kind == "markdraw");
        var explicitFileToken = InoFilesHandler.ExtractFileNameToken(text);
        var activeDoc = !string.IsNullOrWhiteSpace(explicitFileToken) && MarkdrawApp.IsMarkdrawExtension(explicitFileToken)
            ? MarkdrawApp.NormalizeDocumentPath(explicitFileToken)
            : existingCard?.Columns is { Length: >= 1 } && !string.IsNullOrWhiteSpace(existingCard.Columns[0])
                ? existingCard.Columns[0]
                : "/work/architecture.markdraw";

        var currentContent = existingCard?.Columns is { Length: >= 4 } && !string.IsNullOrWhiteSpace(existingCard.Columns[3])
            ? existingCard.Columns[3]
            : MarkdrawApp.DefaultArchitectureScene;

        var currentRev = existingCard?.Columns is { Length: >= 3 } && int.TryParse(existingCard.Columns[2], out var parsedRev)
            ? parsedRev
            : 1;

        if (filesVfs is not null)
        {
            try
            {
                if (await filesVfs.ReadTextAsync(activeDoc) is { Length: > 0 } vfsText)
                {
                    currentContent = vfsText;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, "Optional FilesVfs read in ExecuteMarkdrawAsync.");
            }
        }

        string operation;
        string summary;
        string updatedContent = currentContent;
        string? exportPath = null;

        var isExport = text.Contains("export", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("excalidraw", StringComparison.OrdinalIgnoreCase) || text.Contains("svg", StringComparison.OrdinalIgnoreCase));
        var isFromData = (text.Contains("diagram", StringComparison.OrdinalIgnoreCase) || text.Contains("lineage", StringComparison.OrdinalIgnoreCase) || text.Contains("flow", StringComparison.OrdinalIgnoreCase))
            && (text.Contains("data", StringComparison.OrdinalIgnoreCase) || text.Contains("table", StringComparison.OrdinalIgnoreCase) || text.Contains("chart", StringComparison.OrdinalIgnoreCase) || text.Contains("pipeline", StringComparison.OrdinalIgnoreCase) || text.Contains("opportunities", StringComparison.OrdinalIgnoreCase));
        var isOpenOnly = (text.StartsWith("open ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("show ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("read ", StringComparison.OrdinalIgnoreCase))
            && !text.Contains("add ", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("connect", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("remove", StringComparison.OrdinalIgnoreCase);

        if (isExport)
        {
            operation = "export";
            var format = text.Contains("svg", StringComparison.OrdinalIgnoreCase) ? "svg" : "excalidraw";
            var baseStem = Path.GetFileNameWithoutExtension(activeDoc);
            exportPath = $"/work/{baseStem}.{format}";
            var elementsForExport = MarkdrawApp.ParseSceneElements(currentContent);
            var payload = format == "svg"
                ? MarkdrawApp.RenderSvgPreview(elementsForExport)
                : MarkdrawApp.RenderExcalidrawJson(elementsForExport);
            if (filesVfs is not null)
            {
                var writtenExport = await filesVfs.WriteTextAsync(exportPath, payload);
                exportPath = writtenExport.VfsPath;
            }
            summary = $"Exported {elementsForExport.Length} Markdraw element(s) to {exportPath} ({format})";
        }
        else if (isFromData)
        {
            operation = "from-data";
            var activeTable = currentState.ActiveCards.LastOrDefault(c => c.Kind == "table" && c.Columns is { Length: > 0 });
            var activeChart = currentState.ActiveCards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });
            var dataTitle = activeChart?.Title ?? activeTable?.Title ?? "Pipeline Opportunities by Amount";
            var dataCols = activeTable?.Columns ?? ["Opportunity", "Account", "Stage", "Amount"];
            var rowsSummary = activeChart?.ChartPoints is { Length: > 0 } pts
                ? $"{pts.Length} series points · Top: {pts[0].Label} (€{pts[0].Value:N0})"
                : "5 rows · Total €627,700";
            activeDoc = !string.IsNullOrWhiteSpace(explicitFileToken) ? MarkdrawApp.NormalizeDocumentPath(explicitFileToken) : "/work/data-lineage.markdraw";
            updatedContent = MarkdrawApp.BuildDataLineageMarkdraw(dataTitle, dataCols, rowsSummary, activeTable?.SourceNeuronUri ?? "neuron://local/postgres/main");
            currentRev++;
            summary = $"Generated interactive Markdraw data-lineage canvas for '{dataTitle}' in {activeDoc}";
        }
        else if (isOpenOnly)
        {
            operation = "open";
            summary = $"Opened Markdraw Studio canvas {activeDoc}";
        }
        else
        {
            operation = "patch-elements";
            var (merged, patchSummary) = MarkdrawApp.ApplyCoDrawingPatch(currentContent, text, explicitDsl: null, removeIdsCsv: null);
            updatedContent = merged;
            currentRev++;
            summary = $"{patchSummary} on {activeDoc}";
        }

        string sourceUri = $"neuron://local/apps/markdraw#{Path.GetFileName(activeDoc)}";
        if (filesVfs is not null)
        {
            try
            {
                var written = await filesVfs.WriteTextAsync(activeDoc, updatedContent);
                activeDoc = written.VfsPath;
                sourceUri = written.NeuronUri;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                onRecoverableError?.Invoke(error, "Optional FilesVfs write in ExecuteMarkdrawAsync.");
            }
        }

        try
        {
            if (tryGetApp("apps/markdraw") is { } markdrawNeuron)
            {
                await markdrawNeuron.Request(operation, text);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            onRecoverableError?.Invoke(error, "MarkdrawApp neuron is optional when running InoApp in standalone unit tests.");
        }

        var elements = MarkdrawApp.ParseSceneElements(updatedContent);
        var cardId = existingCard?.Id ?? $"markdraw-{currentState.Turns.Length + 1}";
        var formatKind = activeDoc.EndsWith(".excalidraw", StringComparison.OrdinalIgnoreCase) ? "excalidraw" : "markdraw";

        var markdrawCard = new InoCard(
            cardId,
            "markdraw",
            $"Markdraw Canvas · {Path.GetFileName(activeDoc)} (rev {currentRev})",
            $"{summary} · {elements.Length} element(s) on shared canvas",
            sourceUri,
            "ready",
            $"neuron://local/apps/ino#{cardId}",
            Columns: [activeDoc, formatKind, $"{currentRev}", updatedContent, exportPath ?? string.Empty],
            Rows: elements.Select(e => new[]
            {
                e.Id,
                e.Kind,
                e.Label,
                $"{e.X},{e.Y}",
                $"{e.Width}x{e.Height}",
                e.FromId ?? string.Empty,
                e.ToId ?? string.Empty,
                e.Color ?? "#10b981",
            }).ToArray());

        var updatedCards = existingCard is not null
            ? currentState.ActiveCards.Select(c => c.Id == existingCard.Id ? markdrawCard : c).ToArray()
            : [.. currentState.ActiveCards, markdrawCard];

        var elementBullets = string.Join(
            ", ",
            elements.Take(6).Select(e => e.Kind == "arrow" ? $"`{e.FromId} -> {e.ToId}`" : $"`{e.Id}` ({e.Label})"));
        var exportLine = !string.IsNullOrWhiteSpace(exportPath) ? $"\n- **Exported Artifact**: `{exportPath}`" : string.Empty;
        var reply =
            $"Ino ({settings.DefaultModel}): {summary}.\n\n"
            + $"- **Canvas Document**: `{activeDoc}` (`{sourceUri}` · rev {currentRev})\n"
            + $"- **Elements ({elements.Length})**: {elementBullets}{exportLine}\n"
            + "- **Live Co-Drawing**: Draw directly on the Markdraw canvas card below or ask me to add, connect, or remove nodes.";

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.markdraw", $"User: {text} | Op: {operation} | Doc: {activeDoc} (rev {currentRev})", importance: 7);
        }

        if (knowledgeStore is not null)
        {
            try
            {
                await knowledgeStore.RecordNodeAsync(new KnowledgeNode(
                    $"markdraw:{activeDoc}",
                    activeDoc,
                    "markdraw",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["operation"] = operation,
                        ["elements"] = $"{elements.Length}",
                        ["uri"] = sourceUri,
                    }));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }
        }

        var routeHint = $"routed:markdraw.{operation}";
        var title = currentState.Turns.Length == 0
            ? (text.Length <= 52 ? text : text[..52])
            : currentState.Title;
        var nextTurns = currentState.Turns.Append(new InoTurn(text, reply, routeHint, recalled.Length)).ToArray();

        return currentState with
        {
            Title = title,
            Status = $"{summary} (rev {currentRev})",
            Turns = nextTurns,
            Cards = updatedCards,
        };
    }
}
