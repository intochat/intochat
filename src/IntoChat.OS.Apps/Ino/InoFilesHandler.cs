using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Apps.Ino;

internal enum InoFileActionKind
{
    InspectOrSummarize,
    ListWorkspace,
    WriteFile,
}

internal sealed record InoFileIntent(
    InoFileActionKind Kind,
    string FileName,
    string ResourceUri,
    string? PreviewSnippet,
    string CleanPrompt,
    string? WriteContent = null);

internal static class InoFilesHandler
{
    public static bool TryParseFileIntent(string text, out InoFileIntent intent)
    {
        intent = new InoFileIntent(InoFileActionKind.InspectOrSummarize, string.Empty, string.Empty, null, text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleanPrompt = text.Trim();
        string? previewSnippet = null;
        const string previewMarker = "\n\n[File Preview]\n";
        var markerIdx = text.IndexOf(previewMarker, StringComparison.Ordinal);
        if (markerIdx >= 0)
        {
            cleanPrompt = text[..markerIdx].Trim();
            previewSnippet = text[(markerIdx + previewMarker.Length)..].Trim();
        }

        // 1. Check for workspace listing requests
        if (markerIdx < 0 && IsListFilesRequest(cleanPrompt))
        {
            intent = new InoFileIntent(
                InoFileActionKind.ListWorkspace,
                "workspace",
                "neuron://owner/local-disk.files/workspace",
                null,
                cleanPrompt);
            return true;
        }

        // 2. Check for file write / creation requests
        if (markerIdx < 0 && TryExtractWriteFileRequest(cleanPrompt, out var writeName, out var writeContent))
        {
            intent = new InoFileIntent(
                InoFileActionKind.WriteFile,
                writeName,
                $"neuron://owner/local-disk.files/{writeName}",
                writeContent,
                cleanPrompt,
                writeContent);
            return true;
        }

        // 3. Check for explicit handoff, URI, @-mention, or read/inspect file requests
        var hasExplicitFileVerb = (cleanPrompt.StartsWith("read ", StringComparison.OrdinalIgnoreCase)
                || cleanPrompt.StartsWith("inspect ", StringComparison.OrdinalIgnoreCase)
                || cleanPrompt.StartsWith("summarize file ", StringComparison.OrdinalIgnoreCase)
                || cleanPrompt.StartsWith("open file ", StringComparison.OrdinalIgnoreCase))
            && ExtractFileNameToken(cleanPrompt) is not null;

        var isFileHandoff = cleanPrompt.StartsWith("Explain and summarize ", StringComparison.OrdinalIgnoreCase)
            || cleanPrompt.Contains(".files/", StringComparison.OrdinalIgnoreCase)
            || cleanPrompt.StartsWith("@", StringComparison.Ordinal)
            || markerIdx >= 0
            || hasExplicitFileVerb;

        if (!isFileHandoff)
        {
            return false;
        }

        string resourceUri;
        var uriStart = cleanPrompt.IndexOf("neuron://", StringComparison.OrdinalIgnoreCase);
        if (uriStart >= 0)
        {
            var end = cleanPrompt.IndexOfAny([' ', ')', '\r', '\n', ']'], uriStart);
            resourceUri = end > uriStart ? cleanPrompt[uriStart..end] : cleanPrompt[uriStart..];
        }
        else
        {
            resourceUri = "neuron://owner/local-disk.files/workspace";
        }

        var fileName = string.Empty;
        if (cleanPrompt.StartsWith("Explain and summarize ", StringComparison.OrdinalIgnoreCase) && uriStart > "Explain and summarize ".Length)
        {
            fileName = cleanPrompt["Explain and summarize ".Length..uriStart].Trim(' ', '(', ')', ':');
        }
        else if (cleanPrompt.StartsWith("@", StringComparison.Ordinal))
        {
            var space = cleanPrompt.IndexOf(' ');
            fileName = (space > 1 ? cleanPrompt[1..space] : cleanPrompt[1..]).Trim();
        }
        else if (ExtractFileNameToken(cleanPrompt) is { } tokenName)
        {
            fileName = tokenName;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            var slash = resourceUri.LastIndexOf('/');
            fileName = slash >= 0 && slash + 1 < resourceUri.Length ? resourceUri[(slash + 1)..] : "resource";
        }

        intent = new InoFileIntent(
            InoFileActionKind.InspectOrSummarize,
            fileName,
            resourceUri,
            previewSnippet,
            cleanPrompt);
        return true;
    }

    public static async Task<InoAppState> ExecuteAsync(
        InoAppState currentState,
        InoFileIntent intent,
        IFilesVfsService? vfs,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        IMemory? memory,
        IKnowledgeStore? knowledgeStore,
        Func<string, Task<string?>> generateModelReplyAsync,
        CancellationToken ct = default)
    {
        return intent.Kind switch
        {
            InoFileActionKind.ListWorkspace => await ExecuteListFilesAsync(currentState, intent.CleanPrompt, vfs, settings, recalled, memory, ct),
            InoFileActionKind.WriteFile => await ExecuteWriteFileAsync(currentState, intent, vfs, settings, recalled, memory, knowledgeStore, ct),
            _ => await ExecuteInspectFileAsync(currentState, intent, vfs, settings, recalled, memory, knowledgeStore, generateModelReplyAsync, ct),
        };
    }

    private static async Task<InoAppState> ExecuteListFilesAsync(
        InoAppState currentState,
        string prompt,
        IFilesVfsService? vfs,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        IMemory? memory,
        CancellationToken ct)
    {
        var brainId = vfs?.BrainId ?? "owner";
        var files = vfs is null ? [] : await vfs.ListFilesAsync(ct: ct);
        string reply;
        if (files.Count == 0)
        {
            reply = $"Ino ({settings.DefaultModel}): Your **Files** workspace (`neuron://{brainId}/apps/files`) is connected and currently has 0 indexed files. Ask me to create a file or open **Files** to browse local or cloud storages.";
        }
        else
        {
            var bulletList = string.Join(
                "\n",
                files.Take(12).Select(f => $"- **{f.Name}** (`{f.NeuronUri}` · {f.Kind}{(f.Size is { } sz ? $" · {sz:N0} bytes" : string.Empty)})"));
            reply = $"Ino ({settings.DefaultModel}): Found **{files.Count} file(s)** in **Files** (`neuron://{brainId}/apps/files`):\n\n{bulletList}";
        }

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.files", $"Listed {files.Count} workspace files | Prompt: {prompt}", importance: 6);
        }

        var turnsCount = currentState.Turns.Length + 1;
        var cardId = $"file-{turnsCount}";
        var fileCard = new InoCard(
            cardId,
            "file",
            $"Files Workspace ({files.Count} item(s))",
            files.Count > 0 ? string.Join(", ", files.Take(5).Select(f => f.Name)) : "Connected to storage.files",
            $"neuron://{brainId}/apps/files",
            "ready",
            $"neuron://local/apps/ino#{cardId}");

        var nextTitle = currentState.Turns.Length == 0 || currentState.Title == "New chat"
            ? "Files · Workspace"
            : currentState.Title;
        var nextTurns = currentState.Turns.Append(new InoTurn(prompt, reply, "routed:files.resource", recalled.Length)).ToArray();

        return currentState with
        {
            Title = nextTitle,
            Status = $"Listed {files.Count} workspace file(s)",
            Turns = nextTurns,
            Cards = [.. currentState.ActiveCards, fileCard],
        };
    }

    private static async Task<InoAppState> ExecuteWriteFileAsync(
        InoAppState currentState,
        InoFileIntent intent,
        IFilesVfsService? vfs,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        IMemory? memory,
        IKnowledgeStore? knowledgeStore,
        CancellationToken ct)
    {
        var content = intent.WriteContent ?? string.Empty;
        var written = vfs is not null
            ? await vfs.WriteTextAsync(intent.FileName, content, ct)
            : new WorkspaceFileEntry(
                intent.FileName,
                "local-disk",
                intent.FileName,
                "file",
                System.Text.Encoding.UTF8.GetByteCount(content),
                intent.ResourceUri,
                $"/work/{intent.FileName}");

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.file.write", $"Created file {written.Name} ({written.NeuronUri})", importance: 7);
        }

        if (knowledgeStore is not null)
        {
            try
            {
                await knowledgeStore.RecordNodeAsync(
                    new KnowledgeNode(
                        $"file:{written.Name}",
                        written.Name,
                        "file",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["uri"] = written.NeuronUri,
                            ["path"] = written.VfsPath,
                        }),
                    ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }
        }

        var reply =
            $"Ino ({settings.DefaultModel}): Saved **{written.Name}** ({written.Size ?? 0} bytes) to **Files** (`{written.NeuronUri}`).\n\n"
            + $"- **VFS Path**: `{written.VfsPath}`\n"
            + $"- **Neuron URI**: `{written.NeuronUri}`";

        var turnsCount = currentState.Turns.Length + 1;
        var cardId = $"file-{turnsCount}";
        var fileCard = new InoCard(
            cardId,
            "file",
            written.Name,
            content.Length <= 180 ? content : content[..180] + "…",
            written.NeuronUri,
            "ready",
            $"neuron://local/apps/ino#{cardId}");

        var nextTitle = currentState.Turns.Length == 0 || currentState.Title == "New chat"
            ? $"File · {written.Name}"
            : currentState.Title;
        var nextTurns = currentState.Turns.Append(new InoTurn(intent.CleanPrompt, reply, "routed:files.resource", recalled.Length)).ToArray();

        return currentState with
        {
            Title = nextTitle,
            Status = $"Saved {written.Name} ({written.NeuronUri})",
            Turns = nextTurns,
            Cards = [.. currentState.ActiveCards, fileCard],
        };
    }

    private static async Task<InoAppState> ExecuteInspectFileAsync(
        InoAppState currentState,
        InoFileIntent intent,
        IFilesVfsService? vfs,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        IMemory? memory,
        IKnowledgeStore? knowledgeStore,
        Func<string, Task<string?>> generateModelReplyAsync,
        CancellationToken ct)
    {
        var fileName = intent.FileName;
        var resourceUri = intent.ResourceUri;
        var previewSnippet = intent.PreviewSnippet;

        // Resolve from live Files VFS if preview was not embedded in handoff
        var resolved = vfs is null ? null : await vfs.ResolveFileAsync(!string.IsNullOrWhiteSpace(fileName) ? fileName : resourceUri, ct);
        if (resolved is not null)
        {
            fileName = resolved.Name;
            if (resourceUri == "neuron://owner/local-disk.files/workspace" || string.IsNullOrWhiteSpace(resourceUri))
            {
                resourceUri = resolved.NeuronUri;
            }
        }

        if (string.IsNullOrWhiteSpace(previewSnippet) && vfs is not null)
        {
            previewSnippet = await vfs.ReadTextAsync(!string.IsNullOrWhiteSpace(fileName) ? fileName : resourceUri, ct);
        }

        var promptForModel = string.IsNullOrWhiteSpace(previewSnippet)
            ? intent.CleanPrompt
            : $"{intent.CleanPrompt}\n\nFile contents ({fileName}):\n{previewSnippet}";
        var generated = await generateModelReplyAsync(promptForModel);

        string reply;
        if (!string.IsNullOrWhiteSpace(generated))
        {
            reply = $"Ino ({settings.DefaultModel}): {generated}\n\n- **Resource**: `{fileName}` (`{resourceUri}`)";
        }
        else if (!string.IsNullOrWhiteSpace(previewSnippet))
        {
            var lines = previewSnippet.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var previewExcerpt = string.Join("\n", lines.Take(6).Select(l => $"> {l}"));
            reply =
                $"Ino ({settings.DefaultModel}): Analyzed **{fileName}** (`{resourceUri}`) — {lines.Length} non-empty line(s), {previewSnippet.Length} chars.\n\n"
                + $"{previewExcerpt}\n\n"
                + "**Suggested actions:** Ask me to *extract a table*, *edit in PhotoCraft/FilmCraft*, or *rewrite key sections* from this file.";
        }
        else
        {
            reply =
                $"Ino ({settings.DefaultModel}): Attached **{fileName}** (`{resourceUri}`) from **Files**.\n\n"
                + "- **Source URI**: `" + resourceUri + "`\n"
                + "- **Ready for**: Summarization, key takeaway extraction, table conversion, or cross-app workflow routing.";
        }

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino.file", $"File: {fileName} ({resourceUri}) | Prompt: {intent.CleanPrompt}", importance: 7);
        }

        if (knowledgeStore is not null)
        {
            try
            {
                await knowledgeStore.RecordNodeAsync(
                    new KnowledgeNode(
                        $"file:{fileName}",
                        fileName,
                        "file",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["uri"] = resourceUri,
                        }),
                    ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }
        }

        var turnsCount = currentState.Turns.Length + 1;
        var cardId = $"file-{turnsCount}";
        var summaryText = !string.IsNullOrWhiteSpace(previewSnippet)
            ? (previewSnippet.Length <= 180 ? previewSnippet : previewSnippet[..180] + "…")
            : $"Workspace file resource attached from Files ({resourceUri})";
        var fileCard = new InoCard(
            cardId,
            "file",
            fileName,
            summaryText,
            resourceUri,
            "ready",
            $"neuron://local/apps/ino#{cardId}");

        var nextTitle = currentState.Turns.Length == 0 || currentState.Title == "New chat"
            ? $"File · {fileName}"
            : currentState.Title;
        var nextTurns = currentState.Turns.Append(new InoTurn(intent.CleanPrompt, reply, "routed:files.resource", recalled.Length)).ToArray();

        return currentState with
        {
            Title = nextTitle,
            Status = $"Attached {fileName} ({resourceUri})",
            Turns = nextTurns,
            Cards = [.. currentState.ActiveCards, fileCard],
        };
    }

    private static bool IsListFilesRequest(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower is "list files" or "show files" or "files" or "ls"
            || lower.Contains("list workspace files", StringComparison.Ordinal)
            || lower.Contains("list files in", StringComparison.Ordinal)
            || lower.Contains("what files are in", StringComparison.Ordinal)
            || lower.Contains("show my files", StringComparison.Ordinal)
            || lower.Contains("show workspace files", StringComparison.Ordinal);
    }

    private static bool TryExtractWriteFileRequest(string text, out string fileName, out string content)
    {
        fileName = string.Empty;
        content = string.Empty;

        if (!text.StartsWith("create file ", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("write file ", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("save file ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extractedName = ExtractFileNameToken(text);
        if (string.IsNullOrWhiteSpace(extractedName))
        {
            return false;
        }

        fileName = extractedName;
        var nameIdx = text.IndexOf(extractedName, StringComparison.OrdinalIgnoreCase);
        var afterName = nameIdx >= 0 ? text[(nameIdx + extractedName.Length)..].Trim() : string.Empty;
        content = afterName.StartsWith("with content ", StringComparison.OrdinalIgnoreCase)
            ? afterName["with content ".Length..].Trim(' ', ':', '"', '\'')
            : afterName.StartsWith("with ", StringComparison.OrdinalIgnoreCase)
                ? afterName["with ".Length..].Trim(' ', ':', '"', '\'')
                : afterName.StartsWith(':')
                    ? afterName[1..].Trim()
                    : afterName;

        return true;
    }

    internal static string? ExtractFileNameToken(string text)
    {
        string[] extensions = [".txt", ".md", ".json", ".csv", ".cs", ".xml", ".yaml", ".yml", ".log", ".html", ".png", ".jpg", ".jpeg", ".webp", ".psd", ".mp4", ".mov", ".fcproj"];
        foreach (var rawToken in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken.Trim('"', '\'', ',', ';', '(', ')', '[', ']', '`', ':');
            if (extensions.Any(ext => token.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                return token;
            }
        }
        return null;
    }
}
