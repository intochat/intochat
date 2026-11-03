using IntoChat.OS.Modules.Apps;
using System.Text;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Tasks;

namespace IntoChat.OS.Apps.Ino;

internal sealed record DiscoveredAppCapability(
    string AppId,
    string Title,
    string Description,
    AppOperation[] Operations);

internal static class InoCapabilityCatalog
{
    private static readonly AppBundle FilesBundle = new(
        "files", "3.0.1",
        new AppManifest(
            Title: "Files",
            Description: "Browse, search, create and manage files across connected storages, with safe confirmations.",
            Operations:
            [
                new("open", "Show connected storages."),
                new("storage", "Show the roots of the storage at Input.Index."),
                new("browse", "Open the indexed folder in the current listing."),
                new("select", "Select the indexed item in the current listing."),
                new("search", "Search names in the current folder, or set Input.Everywhere for all storages."),
                new("more", "Load the next page of results."),
                new("preview", "Open the selected folder or preview the selected file."),
                new("up", "Browse the parent folder."),
                new("mkdir", "Create Input.Name; include ExpectedRevision."),
                new("write", "Save Input.Text as Input.Name; include ExpectedRevision."),
                new("rename", "Rename the selected item to Input.Name; include ExpectedRevision."),
                new("delete", "Move the selected item to trash after confirmation; include ExpectedRevision."),
                new("copy", "Remember the selected source for copying; include ExpectedRevision."),
                new("move", "Remember the selected source for moving; include ExpectedRevision."),
                new("paste", "Copy or move the saved source into this folder; include ExpectedRevision."),
                new("fail", "Use the default stop-on-conflict policy; include ExpectedRevision."),
                new("keep-both", "Keep both items on a name conflict; include ExpectedRevision."),
                new("replace", "Replace conflicts after confirmation; include ExpectedRevision."),
                new("dry-run", "Toggle dry-run planning; include ExpectedRevision."),
                new("cancel", "Cancel the indexed running operation; include ExpectedRevision."),
                new("ask-ino", "Open Ino with the selected file's neuron:// reference pre-filled."),
            ],
            Settings: [], Slots: [],
            Toolbar: [new("up", Icon: "arrow-up", Label: "Up"), new("open", Icon: "folder", Label: "Storages")],
            Requires: new(Capabilities: ["storage@1"])),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly (string AppId, AppBundle FallbackBundle)[] KnownApps =
    [
        ("files", FilesBundle),
        ("photocraft", IntoChat.OS.Apps.PhotoCraft.PhotoCraftApp.Bundle),
        ("filmcraft", IntoChat.OS.Apps.FilmCraft.FilmCraftApp.Bundle),
        ("settings", IntoChat.OS.Apps.Settings.SettingsApp.Bundle),
        ("creator", IntoChat.OS.Apps.Creator.CreatorApp.Bundle),
        ("gallery", IntoChat.OS.Apps.Gallery.GalleryApp.Bundle),
        ("post", IntoChat.OS.Apps.Post.PostApp.Bundle),
        ("ino", InoApp.Bundle),
    ];

    public static async Task<IReadOnlyList<DiscoveredAppCapability>> DiscoverAppsAsync(
        Func<string, IApp?> tryGetApp,
        Func<IAppStore?>? tryGetAppStore = null)
    {
        var results = new List<DiscoveredAppCapability>(KnownApps.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (appId, fallbackBundle) in KnownApps)
        {
            var manifest = fallbackBundle.Manifest;
            try
            {
                if (tryGetApp($"apps/{appId}") is { } liveApp)
                {
                    var snapshot = await liveApp.Read();
                    if (!string.IsNullOrWhiteSpace(snapshot.Manifest.Title) && snapshot.Manifest.Operations is { Length: > 0 })
                    {
                        manifest = snapshot.Manifest;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }

            seen.Add(appId);
            results.Add(new DiscoveredAppCapability(
                appId,
                manifest.Title,
                manifest.Description,
                manifest.Operations));
        }

        if (tryGetAppStore?.Invoke() is { } store)
        {
            try
            {
                var entries = await store.List();
                foreach (var entry in entries.Where(e => e.Installed && !string.IsNullOrWhiteSpace(e.InstalledName)))
                {
                    var installedId = entry.InstalledName!;
                    if (!seen.Add(installedId))
                    {
                        continue;
                    }

                    results.Add(new DiscoveredAppCapability(
                        installedId,
                        entry.Title,
                        entry.Description,
                        []));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }
        }

        return results;
    }

    public static IReadOnlyDictionary<string, string> BuildCandidateDescriptions(
        IReadOnlyList<DiscoveredAppCapability>? discoveredApps = null)
    {
        var byId = (discoveredApps ?? KnownApps.Select(k => new DiscoveredAppCapability(k.AppId, k.FallbackBundle.Manifest.Title, k.FallbackBundle.Manifest.Description, k.FallbackBundle.Manifest.Operations)).ToArray())
            .ToDictionary(a => a.AppId, StringComparer.OrdinalIgnoreCase);

        string DescribeApp(string id, string usageConstraint)
        {
            if (!byId.TryGetValue(id, out var cap))
            {
                return usageConstraint;
            }

            var ops = cap.Operations.Length > 0
                ? " Operations: " + string.Join(", ", cap.Operations.Select(o => $"{o.Name} ({o.Description})")) + "."
                : string.Empty;
            return $"{cap.Title}: {cap.Description}{ops} {usageConstraint}".Trim();
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["photocraft"] = DescribeApp(
                "photocraft",
                "Use ONLY when the user explicitly asks to edit an image/photo, remove background, cutout, select subject/sky, desaturate, flip, rotate, or generate a gradient/poster canvas."),
            ["filmcraft"] = DescribeApp(
                "filmcraft",
                "Use ONLY when the user explicitly asks to edit a video sequence, razor split timeline, ripple trim clips, apply Lumetri color grading, or export MP4 video."),
            ["file.resource"] = DescribeApp(
                "files",
                "Use when the user asks to list workspace files, read/inspect/summarize a file, write/create a file in Files, or passes a neuron://...files URI."),
            ["settings.navigate"] = DescribeApp(
                "settings",
                "Use when the user asks to open or navigate OS Settings, configure local Ollama server or AI models, manage OAuth connections (Google Drive, GitHub, Slack, Notion), API keys, variables, billing, team, or revoke/clear settings."),
            ["database.query"] = "PostgreSQL / Supabase read-only SQL query and data table/chart visualization. Use when the user provides a SELECT query or asks to query/visualize database tables, opportunities, or customer accounts.",
            ["chart.mutation"] = "Mutate or filter an active UI Chart or Table card (e.g. switch chart to pie/line/bar, or filter rows by stage/account).",
            ["chart.question"] = "Answer analytical questions about the currently visible UI Chart or Table (e.g. top 3, highest/lowest values).",
            ["connection.string"] = "Connect a PostgreSQL database using an explicit Host=/Server=/postgres:// connection string.",
            ["app.install"] = "Install or review a Smart App from an intochat.app/a/ link.",
            ["tasks"] = "Create, list, or verify autonomous self-improvement tasks via ITaskEngine.",
            ["general.chat"] = "General conversational response, greetings ('hey', 'hello', 'good morning'), capability/meta questions ('hey, what can you do?', 'help', 'who are you?', 'what apps are installed?'), and general Q&A that do not explicitly invoke a specialized tool operation on a target artifact.",
        };
    }

    public static bool IsCapabilityOrMetaInquiry(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().ToLowerInvariant();
        return normalized.Contains("what can you do", StringComparison.Ordinal)
            || normalized.Contains("what do you do", StringComparison.Ordinal)
            || normalized.Contains("what are your capabilities", StringComparison.Ordinal)
            || normalized.Contains("what capabilities", StringComparison.Ordinal)
            || normalized.Contains("what apps", StringComparison.Ordinal)
            || normalized.Contains("which apps", StringComparison.Ordinal)
            || normalized.Contains("what tools", StringComparison.Ordinal)
            || normalized.Contains("how can you help", StringComparison.Ordinal)
            || normalized.Contains("who are you", StringComparison.Ordinal)
            || normalized is "help" or "help?" or "capabilities" or "hey, what can you do?" or "hey what can you do";
    }

    public static async Task<string> BuildLiveCapabilityContextAsync(
        Func<string, IApp?> tryGetApp,
        Func<IAppStore?>? tryGetAppStore,
        IFilesVfsService? filesVfs,
        IMemory? memory,
        IKnowledgeStore? knowledgeStore,
        ITaskEngine? taskEngine,
        string? connectedDatabase,
        string defaultModel,
        CancellationToken ct = default)
    {
        var apps = await DiscoverAppsAsync(tryGetApp, tryGetAppStore);
        var sb = new StringBuilder();
        sb.AppendLine($"Installed IntoChat OS Brain Capabilities (Model: `{defaultModel}`):");

        foreach (var app in apps)
        {
            var opsList = app.Operations.Length > 0
                ? string.Join(", ", app.Operations.Select(o => $"`{o.Name}`"))
                : "`open`";
            sb.AppendLine($"- **{app.Title}** (`apps/{app.AppId}`): {app.Description} (Operations: {opsList})");
        }

        if (filesVfs is not null)
        {
            try
            {
                var files = await filesVfs.ListFilesAsync(ct: ct);
                if (files.Count > 0)
                {
                    var sample = string.Join(", ", files.Take(5).Select(f => $"`{f.Name}`"));
                    sb.AppendLine($"- **Live Files VFS (`storage.files`)**: {files.Count} workspace file(s) available ({sample}).");
                }
                else
                {
                    sb.AppendLine("- **Live Files VFS (`storage.files`)**: Connected and ready to list, read, inspect, or write files.");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }
        }

        if (memory is not null || knowledgeStore is not null)
        {
            sb.AppendLine("- **Memory & Knowledge Graph (`IMemory` / `IKnowledgeStore`)**: Episodic recall, semantic facts, and graph relationships active.");
        }

        if (taskEngine is not null)
        {
            try
            {
                var tasks = await taskEngine.ListTasksAsync(ct: ct);
                sb.AppendLine($"- **Task Engine (`ITaskEngine`)**: Self-improvement task tracking online ({tasks.Count} active/recorded task(s)).");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
            }
        }

        var dbStatus = string.IsNullOrWhiteSpace(connectedDatabase) ? "Ready to connect PostgreSQL (`connect-db` / `query-db`)" : $"Connected to `{connectedDatabase}`";
        sb.AppendLine($"- **Database & Charts**: {dbStatus}.");

        return sb.ToString().Trim();
    }
}
