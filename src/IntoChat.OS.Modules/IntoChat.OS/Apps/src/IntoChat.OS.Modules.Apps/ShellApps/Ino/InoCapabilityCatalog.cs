using System.Text;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Tasks;

namespace IntoChat.OS.Modules.Apps;

internal sealed record DiscoveredAppCapability(
    string AppId,
    string Title,
    string Description,
    AppOperation[] Operations);

internal static class InoCapabilityCatalog
{
    private static AppBundle ResolveBundle(string typeName, string appName, string title, string description, AppOperation[] operations)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName, throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (type is not null && type.GetProperty("Bundle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) is AppBundle bundle)
        {
            return bundle;
        }
        return new AppBundle(appName, "1.0.0", new AppManifest(title, description, operations, [], [], "icon.svg"), "", new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static readonly (string AppId, AppBundle FallbackBundle)[] KnownApps =
    [
        ("files", FilesRequests.Bundle),
        ("photocraft", ResolveBundle("IntoChat.OS.Apps.PhotoCraft.PhotoCraftApp", "photocraft", "PhotoCraft", "Photoshop-compatible layered image studio for editing images, removing backgrounds, adjusting levels, and generating graphics.", [new("open", "Connect PhotoCraft studio and open or inspect a document."), new("edit", "Edit an image from natural language (e.g. remove background, select subject, desaturate, gradient)."), new("remove-background", "Run AI background removal (layer.removeBackground) on the active or specified image."), new("run-command", "Execute a PhotoCraft engine command ID directly via Native Linux CLI or MCP."), new("switch-mode", "Switch execution backend between native-linux, mcp, and wasm-ui.")])),
        ("filmcraft", ResolveBundle("IntoChat.OS.Apps.FilmCraft.FilmCraftApp", "filmcraft", "FilmCraft", "Premiere-compatible non-linear video editor (VideoCraft / FilmCraft) with full-card WASM Web UI, Native Linux CLI (filmcraft-cli), and MCP Server.", [new("open", "Connect FilmCraft studio and load a sequence."), new("edit", "Edit a video sequence from natural language (razor split, ripple trim, Lumetri color, export)."), new("run-command", "Execute a FilmCraft timeline command ID via Native Linux CLI or MCP."), new("export", "Render and export the active video sequence to H.264 MP4."), new("switch-mode", "Switch execution backend between native-linux, mcp, and wasm-ui.")])),
        ("settings", ShellPrograms.Load("Settings")),
        ("creator", ResolveBundle("IntoChat.OS.Apps.Creator.CreatorApp", "creator", "Creator", "3D DigitalBrain inspector and self-improving C# app loop with Aspire telemetry and BDD verification.", [new("open", "Open the 3D DigitalBrain view and telemetry inspector."), new("inspect", "Inspect Aspire logs, traces, and active neurons for anomalies."), new("prompt", "Create or repair a DigitalBrain App<TState> and verify its [Scenario] tests."), new("language", "Switch the plain-language BDD scenario debugger locale (en, uk, es, de, fr)."), new("rollback", "Roll back the sandbox app to a previous verified version and C# source."), new("hot-restart", "Hot-restart the live interactive sandbox viewport with clean state.")])),
        ("gallery", ResolveBundle("IntoChat.OS.Apps.Gallery.GalleryApp", "gallery", "UI Gallery", "WinUI 3-inspired UI Kit, NodeContract inspector, and UI Identity switcher.", [new("open", "Open the UI Kit gallery catalog."), new("filter", "Filter controls by category or search query."), new("select", "Inspect a control's NodeContract, [Signal]s, and neuron:// URI."), new("switch-identity", "Switch the active StylePreset and BarLayout.")])),
        ("post", ResolveBundle("IntoChat.OS.Apps.Post.PostApp", "post", "Post", "Public profile and Circle-scoped social post app.", [new("open", "Show profile and posts."), new("publish", "Publish a post to a Circle."), new("update-bio", "Update profile handle, bio, and pinned apps.")])),
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
