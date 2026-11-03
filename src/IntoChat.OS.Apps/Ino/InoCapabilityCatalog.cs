using System.Reflection;
using System.Text;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Tasks;

namespace IntoChat.OS.Apps.Ino;

public sealed record DiscoveredOperationParameter(
    string Name,
    string TypeName,
    bool IsOptional);

public sealed record DiscoveredAppCapability(
    string AppId,
    string Title,
    string Description,
    AppOperation[] Operations,
    IReadOnlyDictionary<string, DiscoveredOperationParameter[]>? OperationParameters = null);

public static class InoCapabilityCatalog
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
        ("markdraw", IntoChat.OS.Apps.Markdraw.MarkdrawApp.Bundle),
        ("photocraft", IntoChat.OS.Apps.PhotoCraft.PhotoCraftApp.Bundle),
        ("filmcraft", IntoChat.OS.Apps.FilmCraft.FilmCraftApp.Bundle),
        ("settings", IntoChat.OS.Apps.Settings.SettingsApp.Bundle),
        ("creator", IntoChat.OS.Apps.Creator.CreatorApp.Bundle),
        ("gallery", IntoChat.OS.Apps.Gallery.GalleryApp.Bundle),
        ("post", IntoChat.OS.Apps.Post.PostApp.Bundle),
        ("ino", InoApp.Bundle),
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, DiscoveredOperationParameter[]>> ReflectedSignalSchemas =
        ReflectCompiledAppSignalSchemas();

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
            ReflectedSignalSchemas.TryGetValue(appId, out var opParams);
            results.Add(new DiscoveredAppCapability(
                appId,
                manifest.Title,
                manifest.Description,
                manifest.Operations,
                opParams));
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

                    ReflectedSignalSchemas.TryGetValue(installedId, out var opParams);
                    results.Add(new DiscoveredAppCapability(
                        installedId,
                        entry.Title,
                        entry.Description,
                        [],
                        opParams));
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
        var byId = (discoveredApps ?? KnownApps.Select(k =>
            {
                ReflectedSignalSchemas.TryGetValue(k.AppId, out var opParams);
                return new DiscoveredAppCapability(
                    k.AppId,
                    k.FallbackBundle.Manifest.Title,
                    k.FallbackBundle.Manifest.Description,
                    k.FallbackBundle.Manifest.Operations,
                    opParams);
            }).ToArray())
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
            ["markdraw"] = DescribeApp(
                "markdraw",
                "Use when the user asks to open Markdraw/Excalidraw, draw or co-draw an architecture diagram, flowchart, ERD, or whiteboard canvas, add/connect/remove nodes on the canvas, diagram a dataset, or save/export .markdraw and .excalidraw files."),
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
            ["database.query"] = "PostgreSQL / Supabase / CSV / JSON data analytics and interactive table/chart visualization. Use when the user provides a SELECT query or asks to query/visualize/analyze database tables, CSV/JSON datasets, opportunities, or customer accounts.",
            ["chart.mutation"] = "Mutate, filter, or group an active UI Chart or Table card (e.g. switch chart to pie/line/bar, filter rows by stage/account, or group by column).",
            ["chart.question"] = "Answer analytical questions about the currently visible UI Chart or Table (e.g. top 3, average, total, highest/lowest values).",
            ["connection.string"] = "Connect a PostgreSQL database using an explicit Host=/Server=/postgres:// connection string.",
            ["app.install"] = "Install or review a Smart App from an intochat.app/a/ link.",
            ["task.create"] = "Create a multi-step autonomous task in ITaskEngine and hand it off to tasks.executor when the user asks to create/start/run a task or workflow.",
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

        var dbStatus = string.IsNullOrWhiteSpace(connectedDatabase) ? "Ready to connect PostgreSQL (`connect-db` / `query-db`) and analyze CSV/JSON files" : $"Connected to `{connectedDatabase}`";
        sb.AppendLine($"- **Database & Charts**: {dbStatus}.");

        return sb.ToString().Trim();
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, DiscoveredOperationParameter[]>> ReflectCompiledAppSignalSchemas()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, DiscoveredOperationParameter[]>>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(InoCapabilityCatalog).Assembly;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(DigitalBrain.App).IsAssignableFrom(type))
            {
                continue;
            }

            var appAttr = type.GetCustomAttribute<DigitalBrain.AppAttribute>(inherit: false);
            if (appAttr is null || string.IsNullOrWhiteSpace(appAttr.Id))
            {
                continue;
            }

            var opMap = new Dictionary<string, DiscoveredOperationParameter[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<DigitalBrain.OnAttribute>(inherit: false) is null)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 1 && typeof(DigitalBrain.Signal).IsAssignableFrom(parameters[0].ParameterType))
                {
                    var signalType = parameters[0].ParameterType;
                    var signalCtor = signalType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                    var signalParams = signalCtor is null
                        ? []
                        : signalCtor.GetParameters()
                            .Select(p => new DiscoveredOperationParameter(
                                p.Name ?? "value",
                                Nullable.GetUnderlyingType(p.ParameterType)?.Name ?? p.ParameterType.Name,
                                p.HasDefaultValue))
                            .ToArray();
                    opMap[ToKebabCase(method.Name)] = signalParams;
                }
            }

            result[appAttr.Id] = opMap;
        }

        return result;
    }

    private static string ToKebabCase(string pascalName)
    {
        if (string.IsNullOrWhiteSpace(pascalName))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(pascalName.Length + 4);
        for (var i = 0; i < pascalName.Length; i++)
        {
            var ch = pascalName[i];
            if (char.IsUpper(ch) && i > 0)
            {
                sb.Append('-');
            }
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
