using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Apps;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Ui = IntoChat.OS.Modules.Flutter.Views.Ui;

namespace IntoChat.OS.Modules.Apps;

internal sealed class CompiledAppsHost(
    IServiceProvider services,
    InProcessAppHost host,
    DigitalBrain.Spi.IClusterListeners listeners,
    ILogger<CompiledAppsHost> logger) : IStartupTask
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private static (string Name, Type AppType, AppBundle Bundle) ResolveHosted(string appName, string typeName, AppBundle fallbackBundle)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName, throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (type is not null && type.GetProperty("Bundle", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is AppBundle bundle)
        {
            return (appName, type, bundle);
        }
        return (appName, type ?? typeof(App), fallbackBundle);
    }

    private static readonly (string Name, Type AppType, AppBundle Bundle)[] Hosted =
    [
        ResolveHosted("ino", "IntoChat.OS.Apps.Ino.InoApp", new AppBundle("ino", "3.5.0", new AppManifest("Ino", "Talk to your brain, use its capabilities, query databases, and create apps.", [new("open", "Show Ino."), new("send", "Route or answer a message."), new("stop", "Stop the running turn."), new("smart", "Turn smart mode on or off."), new("connect-db", "Connect a PostgreSQL database via connection string."), new("query-db", "Execute a read-only SQL query against the connected database."), new("new", "Start a new conversation."), new("new-conversation", "Alias for new."), new("voice", "Begin voice input."), new("make", "Apply a sentence to the focused app, or Scratch.")], [new("Model", "Model catalog id; empty uses the default.", ""), new("RouteGate", "Confidence required for a direct route.", "0.8"), new("IntendGate", "Seconds to wait for Memory's brief before answering without it.", "8")], [], "icon.svg"), "", new Dictionary<string, string>(StringComparer.Ordinal))),
        ResolveHosted("settings", "IntoChat.OS.Apps.Settings.SettingsApp", new AppBundle("settings", "2.1.0", new AppManifest(
            Title: "Settings",
            Description: "Your preferences, models, keys, connections and archived chats.",
            Operations:
            [
                new("open", "Show Settings on the tab the input names (general when empty or unknown)."),
                new("read", "Publish the current preferences to {app}/preferences."),
                new("refresh-connections", "Show the brain's connections and the host's connector offers again."),
                new("save-preferences", "Save DisplayName and Theme with the Baseline snapshot from preferences/baseline. Reject stale drafts and preserve invalid drafts."),
                new("reload-preferences", "Discard editable drafts and load the stored preferences."),
                new("reconcile-preferences", "Refresh clean drafts from stored preferences; retain unsaved edits and report conflicts."),
            ],
            Settings: [],
            Slots: []), "", new Dictionary<string, string>(StringComparer.Ordinal))),
    ];

    private static readonly IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)> HostedByName =
        Hosted.ToDictionary(item => item.Name, StringComparer.Ordinal);

    private static (string Name, Type AppType, AppBundle Bundle) ResolveSpec(string appName) => appName switch
    {
        "ino" => ResolveHosted("ino", "IntoChat.OS.Apps.Ino.InoApp", HostedByName["ino"].Bundle),
        "settings" => ResolveHosted("settings", "IntoChat.OS.Apps.Settings.SettingsApp", HostedByName["settings"].Bundle),
        _ => HostedByName[appName],
    };

    private readonly ConcurrentDictionary<string, IFilesVfsService> _vfsByBrain = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ensuredBrains = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    private void TriggerEnsureAll(string brain)
    {
        if (!string.IsNullOrWhiteSpace(brain) && _ensuredBrains.TryAdd(brain, 0))
        {
            _ = Task.Run(() => EnsureAllAsync(brain), CancellationToken.None);
        }
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        _ = await listeners.Listen<Activated>(nameof(CompiledAppsHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ensuredBrains.TryAdd(brain, 0);
            _ = Task.Run(() => EnsureAllAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<IntoChat.OS.Modules.Flutter.Workspace.Signals.WorkspaceChanged>(nameof(CompiledAppsHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            TriggerEnsureAll(brain);
            _ = Task.Run(() => EnsureDesktopViewAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<AppRequested>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            TriggerEnsureAll(publisher.Brain.Value);
            if (!publisher.Key.StartsWith("apps/", StringComparison.Ordinal)) { return Task.CompletedTask; }
            var appName = publisher.Key["apps/".Length..];
            if (appName == "brain") { return Task.CompletedTask; }
            if (appName == "desktop")
            {
                _ = Task.Run(() => EnsureDesktopViewAsync(publisher.Brain.Value), CancellationToken.None);
            }
            _ = Task.Run(() => HandleAppRequestedAsync(publisher.Brain.Value, appName, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<ComposerSubmitted>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            TriggerEnsureAll(publisher.Brain.Value);
            if (publisher.Key != "apps/ino/composer") { return Task.CompletedTask; }
            _ = Task.Run(() => HandleComposerSubmittedAsync(publisher.Brain.Value, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<ViewAction>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            TriggerEnsureAll(publisher.Brain.Value);
            if (!publisher.Key.StartsWith("apps/", StringComparison.Ordinal)) { return Task.CompletedTask; }
            var rest = publisher.Key["apps/".Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0) { return Task.CompletedTask; }
            var appName = rest[..slash];
            if (appName == "brain") { return Task.CompletedTask; }
            if (appName == "desktop")
            {
                _ = Task.Run(() => HandleDesktopViewActionAsync(publisher.Brain.Value, signal, token), CancellationToken.None);
                return Task.CompletedTask;
            }
            if (!HostedByName.ContainsKey(appName))
            {
                _ = Task.Run(async () =>
                {
                    if (await HostCaller.ForBrain(services, publisher.Brain.Value) is { } caller)
                    {
                        CallerContextStamper.Stamp(caller);
                    }
                    var app = SiloNeurons.Get<IApp>(services, IApp.Key(publisher.Brain.Value, appName));
                    var parts = signal.Action.Split(':', 2, StringSplitOptions.TrimEntries);
                    await app.Request(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
                }, CancellationToken.None);
                return Task.CompletedTask;
            }
            _ = Task.Run(() => HandleViewActionAsync(publisher.Brain.Value, appName, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        TriggerEnsureAll("owner");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAllAsync(string brain)
    {
        foreach (var (name, _, _) in Hosted) { await EnsureAppAsync(brain, name); }
        await EnsureDesktopViewAsync(brain);
    }

    private static string TitleForDesktopApp(string appId) => appId switch
    {
        "ino" => "Ino",
        "files" => "Files",
        "data" => "Data Studio",
        "markdraw" => "Markdraw",
        "photocraft" => "PhotoCraft",
        "filmcraft" => "FilmCraft",
        "creator" => "Creator",
        "gallery" => "Gallery",
        "post" => "Post",
        "brain" => "Brain",
        "settings" => "Settings",
        "circle" or "circles" => "Circles",
        "workspace" => "Workspace",
        _ => string.IsNullOrEmpty(appId) ? "App" : char.ToUpperInvariant(appId[0]) + appId[1..],
    };

    private async Task EnsureDesktopViewAsync(string brain)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var workspace = SiloNeurons.Get<IntoChat.OS.Modules.Flutter.Workspace.IWorkspace>(services, IntoChat.OS.Modules.Flutter.Workspace.IWorkspace.Key(brain));
            var state = await workspace.Read();
            var widgetsText = (await SiloNeurons.Get<IText>(services, $"{brain}/apps/desktop/widgets").Read()).Markdown ?? string.Empty;

            var activeWindows = state.Windows
                .Where(w => w.Tabs is { Length: > 0 } && w.Tabs.Any(t => IApp.NameOf(brain, t.App) is not "desktop"))
                .ToArray();

            foreach (var window in activeWindows)
            {
                var activeTab = window.Tabs.FirstOrDefault(t => t.Id == window.ActiveTabId) ?? window.Tabs[^1];
                var appId = IApp.NameOf(brain, activeTab.App) ?? activeTab.Id;
                var viewport = SiloNeurons.Get<IntoChat.OS.Modules.Flutter.Viewport.IViewport>(services, $"{brain}/apps/desktop/window/{window.Id}");
                var currentVp = await viewport.Read();
                var targetRef = new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef(activeTab.Id, $"{brain}/apps/{appId}", string.IsNullOrWhiteSpace(activeTab.Variant) ? "full" : activeTab.Variant, string.Empty);
                if (currentVp.Target != targetRef) { await viewport.Set(targetRef, currentVp.Revision); }
                _ = EnsureOpenAppViewAsync(brain, appId, CancellationToken.None);
            }

            UiNode BuildWindowFrame(IntoChat.OS.Modules.Flutter.Workspace.WorkspaceWindow window)
            {
                var winId = window.Id;
                var activeIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
                var activeTab = window.Tabs[activeIndex];
                var appId = IApp.NameOf(brain, activeTab.App) ?? activeTab.Id;
                var title = TitleForDesktopApp(appId);
                var isPinned = state.Pins.Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(IApp.NameOf(brain, p.App), appId, StringComparison.OrdinalIgnoreCase));
                var isFav = state.Favourites.Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));

                UiNode[] chromeButtons =
                [
                    Ui.Button($"focus-{winId}", title, $"focus:{winId}", style: UiStyle.Ghost),
                    Ui.IconButton($"newtab-{winId}", "plus", "New tab", $"newtab:{winId}"),
                    Ui.IconButton($"star-{winId}", "star", isFav ? "Remove from favourites" : "Add to favourites", $"favourite:{winId}"),
                    .. appId == "ino"
                        ? Array.Empty<UiNode>()
                        : [isPinned
                            ? Ui.IconButton($"unpin-{winId}", "pin-off", "Unpin", $"unpin:{appId}")
                            : Ui.IconButton($"pin-{winId}", "pin", "Pin", $"pin:{appId}")],
                    Ui.IconButton($"close-{winId}", "x", "Close tab", $"close:{winId}/{activeIndex}"),
                ];
                var tabNodes = window.Tabs.Select((tab, idx) =>
                {
                    var tabAppId = IApp.NameOf(brain, tab.App) ?? tab.Id;
                    return Ui.Tab($"tab-{winId}-{idx}", TitleForDesktopApp(tabAppId), $"tab:{winId}/{idx}", idx == activeIndex);
                }).ToArray();

                return Ui.Column($"frame-{winId}",
                [
                    Ui.Row($"chrome-{winId}", chromeButtons, gap: 4, extents: [0, .. chromeButtons.Skip(1).Select(_ => 32.0)]),
                    Ui.Tabs($"tabs-{winId}", tabNodes),
                    Ui.Reference($"content-{winId}", new(UIVocabulary.ViewportType, $"{brain}/apps/desktop/window/{winId}")),
                ], gap: 0, extents: [40, 38, 0]);
            }

            UiNode windows;
            if (activeWindows.Length == 0)
            {
                windows = Ui.Column("windows", []);
            }
            else
            {
                var rows = activeWindows
                    .Chunk(3)
                    .Select((row, i) => Ui.Row($"windows-{i}", [.. row.Select(BuildWindowFrame)]))
                    .ToArray();
                windows = Ui.Column("windows", rows, extents: [.. rows.Select(_ => 0.0)]);
            }

            var dock = Ui.Row("dock",
            [
                Ui.Row("apps",
                [
                    Ui.Button("dock-ino", "Ino", "open:ino", style: UiStyle.Primary, icon: "ino"),
                    Ui.Button("dock-files", "Files", "open:files", style: UiStyle.Ghost),
                    Ui.Button("dock-data", "Data Studio", "open:data", style: UiStyle.Ghost),
                    Ui.Button("dock-markdraw", "Markdraw", "open:markdraw", style: UiStyle.Ghost),
                    Ui.Button("dock-photocraft", "PhotoCraft", "open:photocraft", style: UiStyle.Ghost),
                    Ui.Button("dock-filmcraft", "FilmCraft", "open:filmcraft", style: UiStyle.Ghost),
                    Ui.Button("dock-creator", "Creator", "open:creator", style: UiStyle.Ghost),
                    Ui.Button("dock-gallery", "Gallery", "open:gallery", style: UiStyle.Ghost),
                    Ui.Button("dock-post", "Post", "open:post", style: UiStyle.Ghost),
                    Ui.Button("dock-brain", "Brain", "open:brain", style: UiStyle.Ghost),
                    Ui.Button("dock-settings", "Settings", "open:settings", style: UiStyle.Ghost),
                    Ui.IconButton("newtab", "plus", "New tab", "newtab"),
                ], gap: 4),
                Ui.Row("right",
                [
                    Ui.Text("usage", "Usage"),
                    Ui.Text("open-widgets", widgetsText),
                    Ui.Button("avatar", "You", "open:circle"),
                    Ui.Button("more-settings", "Settings", "settings"),
                ], gap: 4),
            ], extents: [0, 360]);

            var nextRoot = Ui.Column("desktop", [windows, dock], extents: [0, 68]);
            var view = SiloNeurons.Get<IView>(services, $"{brain}/apps/desktop/view");
            var current = await view.Read();
            if (current?.Root is not null && JsonSerializer.Serialize(current.Root, Json) == JsonSerializer.Serialize(nextRoot, Json))
            {
                return;
            }

            var gen = current is { Generation: > 0 } ? current.Generation : 1L;
            var nextVer = (current?.Generation == gen ? current.Version : 0L) + 1L;
            await view.Publish(new UiView(gen, nextVer, nextRoot));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Initial desktop view seed for {Brain} did not complete.", brain);
        }
    }

    private async Task EnsureOpenAppViewAsync(string brain, string appName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appName) || appName is "desktop" or "workspace" or "brain") { return; }
        try
        {
            var baseCaller = await HostCaller.ForBrain(services, brain);
            if (baseCaller is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            if (appName == "files")
            {
                var filesView = await SiloNeurons.Get<IView>(services, $"{brain}/apps/files/view").Read();
                if (filesView?.Root is null)
                {
                    using var filesScope = baseCaller is not null ? CallerContextStamper.StampScope(HostCaller.AsApp(baseCaller, IApp.Key(brain, "files"))) : null;
                    await SiloNeurons.Get<IFilesWorkspace>(services, $"{brain}/apps/files/workspace").Open(ct);
                }
                return;
            }

            var view = await SiloNeurons.Get<IView>(services, $"{brain}/apps/{appName}/view").Read();
            if (view?.Root is null)
            {
                await HandleAppRequestedAsync(brain, appName, new AppRequested("open", string.Empty, Guid.NewGuid()), ct);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug(error, "Could not warm view for open app {App} in {Brain}.", appName, brain);
        }
    }

    private async Task EnsureShippedDesktopInstalledAsync(string brain)
    {
        var desktopKey = IApp.Key(brain, "desktop");
        var desktopApp = SiloNeurons.Get<IApp>(services, desktopKey);
        var snapshot = await desktopApp.Read();
        if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
        {
            var bundle = ResolveHosted("desktop", "IntoChat.OS.Apps.Desktop.DesktopApp", new AppBundle("desktop", "0.4.0", new AppManifest(
                Title: "Desktop",
                Description: "Your apps in windows, with a dock.",
                Operations:
                [
                    new("start", "Compose the desktop from the open windows and installed apps."),
                    new("layout", "Choose the layout the next connect shows: desktop or classic."),
                    new("open", "Open an app's window beside the others, or focus it (input: the app name). Enforces the window cap."),
                    new("narrow", "The shell's width hint: true below 900 logical pixels, false otherwise."),
                ],
                Settings: [],
                Slots: [],
                Icon: "icon.svg"), "", new Dictionary<string, string>(StringComparer.Ordinal))).Bundle;
            await desktopApp.Import(bundle, new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "desktop", bundle.Version));
        }
    }

    private async Task ApplyDesktopWorkspaceChangeAsync(
        string brain,
        Guid requestId,
        Func<IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState, (IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds)> change)
    {
        var baseCaller = await HostCaller.ForBrain(services, brain);
        if (baseCaller is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
        await EnsureShippedDesktopInstalledAsync(brain);
        var desktopKey = IApp.Key(brain, "desktop");
        var workspace = SiloNeurons.Get<IntoChat.OS.Modules.Flutter.Workspace.IWorkspace>(services, IntoChat.OS.Modules.Flutter.Workspace.IWorkspace.Key(brain));

        IReadOnlyList<string> closedWindowIds = [];
        using (baseCaller is not null ? CallerContextStamper.StampScope(HostCaller.AsApp(baseCaller, desktopKey)) : null)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var state = await workspace.Read();
                var (update, closed) = change(state);
                closedWindowIds = closed;
                if (update is null) { break; }
                try
                {
                    await workspace.Apply(update, $"desktop:{requestId}:{attempt}", state.Revision);
                    break;
                }
                catch (RevisionConflictException) when (attempt == 0)
                {
                }
            }
        }

        foreach (var winId in closedWindowIds)
        {
            try
            {
                var viewport = SiloNeurons.Get<IntoChat.OS.Modules.Flutter.Viewport.IViewport>(services, $"{brain}/apps/desktop/window/{winId}");
                var currentVp = await viewport.Read();
                if (currentVp.Target is not null) { await viewport.Set(null, currentVp.Revision); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogDebug(error, "Could not clear closed viewport {Window} in {Brain}.", winId, brain);
            }
        }
    }

    private static (IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) OpenDesktopAppInState(
        string brain,
        IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState s,
        string rawAppName,
        string? targetWindowId = null,
        bool asNewTab = false)
    {
        var appName = rawAppName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(appName) || appName == "desktop") { return (null, []); }
        var appKey = IApp.Key(brain, appName);
        var windows = s.Windows.Where(w => w.Tabs is { Length: > 0 }).ToList();

        if (!asNewTab)
        {
            foreach (var w in windows)
            {
                if (w.Tabs.FirstOrDefault(t => string.Equals(t.App, appKey, StringComparison.OrdinalIgnoreCase)) is { } existingTab)
                {
                    if (w.ActiveTabId == existingTab.Id && s.FocusedViewId == existingTab.Id)
                    {
                        return (null, []);
                    }
                    var focusedWindows = windows.Select(win => win.Id == w.Id ? win with { ActiveTabId = existingTab.Id } : win).ToArray();
                    return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate([.. focusedWindows.SelectMany(win => win.Tabs)], existingTab.Id, s.Pins)
                    {
                        Windows = focusedWindows,
                        Favourites = s.Favourites,
                    }, []);
                }
            }
        }

        var takenTabIds = windows.SelectMany(w => w.Tabs).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var tabId = appName;
        for (var n = 2; !takenTabIds.Add(tabId); n++)
        {
            tabId = $"{appName}-{n}";
        }
        var newTab = new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef(tabId, appKey, "full", string.Empty);

        if (asNewTab)
        {
            var targetWin = targetWindowId is { Length: > 0 }
                ? windows.FirstOrDefault(w => w.Id == targetWindowId)
                : windows.FirstOrDefault(w => w.Tabs.Any(t => t.Id == s.FocusedViewId)) ?? windows.LastOrDefault();
            if (targetWin is not null && targetWin.Tabs.Length < 8)
            {
                IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef[] updatedTabs = [.. targetWin.Tabs, newTab];
                var updatedWins = windows.Select(w => w.Id == targetWin.Id ? new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceWindow(targetWin.Id, updatedTabs, newTab.Id) : w).ToArray();
                return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate([.. updatedWins.SelectMany(w => w.Tabs)], newTab.Id, s.Pins)
                {
                    Windows = updatedWins,
                    Favourites = s.Favourites,
                }, []);
            }
        }

        if (windows.Count >= 8)
        {
            return (null, []);
        }

        var takenWinIds = windows.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var nextWinId = "w-1";
        for (var n = 1; ; n++)
        {
            var candidate = $"w-{n}";
            if (!takenWinIds.Contains(candidate))
            {
                nextWinId = candidate;
                break;
            }
        }

        IntoChat.OS.Modules.Flutter.Workspace.WorkspaceWindow[] nextWindows = [.. windows, new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceWindow(nextWinId, [newTab], newTab.Id)];
        return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate([.. nextWindows.SelectMany(w => w.Tabs)], newTab.Id, s.Pins)
        {
            Windows = nextWindows,
            Favourites = s.Favourites,
        }, []);
    }

    private static (IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) CloseDesktopTargetInState(
        IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState s,
        string target)
    {
        var slash = target.LastIndexOf('/');
        var winId = slash >= 0 ? target[..slash] : target;
        var indexStr = slash >= 0 ? target[(slash + 1)..] : string.Empty;
        var windows = s.Windows.Where(w => w.Tabs is { Length: > 0 }).ToList();
        var window = windows.FirstOrDefault(w => w.Id == winId);
        if (window is null) { return (null, []); }

        if (slash < 0 || !int.TryParse(indexStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tabIndex))
        {
            tabIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
        }
        if (tabIndex < 0 || tabIndex >= window.Tabs.Length) { return (null, []); }

        if (window.Tabs.Length <= 1 || slash < 0)
        {
            var remaining = windows.Where(w => w.Id != winId).ToArray();
            var nextFocus = remaining.Any(w => w.Tabs.Any(t => t.Id == s.FocusedViewId))
                ? s.FocusedViewId
                : remaining.LastOrDefault()?.ActiveTabId;
            return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate([.. remaining.SelectMany(w => w.Tabs)], nextFocus, s.Pins)
            {
                Windows = remaining,
                Favourites = s.Favourites,
            }, [winId]);
        }

        var closedTabId = window.Tabs[tabIndex].Id;
        var nextTabs = window.Tabs.Where((_, i) => i != tabIndex).ToArray();
        var nextActive = window.ActiveTabId == closedTabId ? nextTabs[Math.Max(0, tabIndex - 1)].Id : window.ActiveTabId;
        var updatedWindows = windows.Select(w => w.Id == winId ? new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceWindow(winId, nextTabs, nextActive) : w).ToArray();
        var focused = s.FocusedViewId == closedTabId ? nextActive : s.FocusedViewId;
        return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate([.. updatedWindows.SelectMany(w => w.Tabs)], focused, s.Pins)
        {
            Windows = updatedWindows,
            Favourites = s.Favourites,
        }, []);
    }

    private static (IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) CloseDesktopAppInState(
        string brain,
        IntoChat.OS.Modules.Flutter.Workspace.WorkspaceState s,
        string rawAppName)
    {
        var appName = rawAppName.Trim().ToLowerInvariant();
        var appKey = IApp.Key(brain, appName);
        var closedIds = new List<string>();
        var nextWindows = new List<IntoChat.OS.Modules.Flutter.Workspace.WorkspaceWindow>();
        var changed = false;

        foreach (var w in s.Windows.Where(w => w.Tabs is { Length: > 0 }))
        {
            var keptTabs = w.Tabs.Where(t => !string.Equals(t.App, appKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (keptTabs.Length != w.Tabs.Length)
            {
                changed = true;
            }
            if (keptTabs.Length == 0)
            {
                closedIds.Add(w.Id);
            }
            else
            {
                var nextActive = keptTabs.Any(t => t.Id == w.ActiveTabId) ? w.ActiveTabId : keptTabs[^1].Id;
                nextWindows.Add(new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceWindow(w.Id, keptTabs, nextActive));
            }
        }

        if (!changed) { return (null, []); }
        var remaining = nextWindows.ToArray();
        var nextFocus = remaining.Any(w => w.Tabs.Any(t => t.Id == s.FocusedViewId))
            ? s.FocusedViewId
            : remaining.LastOrDefault()?.ActiveTabId;
        return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate([.. remaining.SelectMany(w => w.Tabs)], nextFocus, s.Pins)
        {
            Windows = remaining,
            Favourites = s.Favourites,
        }, closedIds);
    }

    private async Task HandleDesktopViewActionAsync(string brain, ViewAction signal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(signal.Action)) { return; }
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var desktopKey = IApp.Key(brain, "desktop");
            var gate = _gates.GetOrAdd(desktopKey, static _ => new SemaphoreSlim(1, 1));
            var forwardAction = false;
            string? openedApp = null;

            await gate.WaitAsync(ct);
            try
            {
                var action = signal.Action.Trim();
                if (action.StartsWith("open:", StringComparison.Ordinal))
                {
                    openedApp = action["open:".Length..].Trim().ToLowerInvariant();
                    await ApplyDesktopWorkspaceChangeAsync(brain, signal.RequestId, s => OpenDesktopAppInState(brain, s, openedApp));
                }
                else if (action.StartsWith("close:", StringComparison.Ordinal))
                {
                    var target = action["close:".Length..].Trim();
                    await ApplyDesktopWorkspaceChangeAsync(brain, signal.RequestId, s => CloseDesktopTargetInState(s, target));
                }
                else if (action.StartsWith("close-app:", StringComparison.Ordinal))
                {
                    var targetApp = action["close-app:".Length..].Trim();
                    await ApplyDesktopWorkspaceChangeAsync(brain, signal.RequestId, s => CloseDesktopAppInState(brain, s, targetApp));
                }
                else if (action.StartsWith("focus:", StringComparison.Ordinal))
                {
                    var winId = action["focus:".Length..].Trim();
                    await ApplyDesktopWorkspaceChangeAsync(brain, signal.RequestId, s =>
                    {
                        var w = s.Windows.FirstOrDefault(win => win.Id == winId);
                        if (w is null || s.FocusedViewId == w.ActiveTabId) { return (null, []); }
                        return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate([.. s.Windows.SelectMany(win => win.Tabs)], w.ActiveTabId, s.Pins)
                        {
                            Windows = s.Windows,
                            Favourites = s.Favourites,
                        }, []);
                    });
                }
                else if (action.StartsWith("tab:", StringComparison.Ordinal))
                {
                    var target = action["tab:".Length..].Trim();
                    var sep = target.LastIndexOf('/');
                    if (sep < 0) { sep = target.LastIndexOf(':'); }
                    if (sep > 0)
                    {
                        var winId = target[..sep];
                        var tabPart = target[(sep + 1)..];
                        await ApplyDesktopWorkspaceChangeAsync(brain, signal.RequestId, s =>
                        {
                            var w = s.Windows.FirstOrDefault(win => win.Id == winId);
                            if (w is null) { return (null, []); }
                            var chosen = int.TryParse(tabPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < w.Tabs.Length
                                ? w.Tabs[idx]
                                : w.Tabs.FirstOrDefault(t => t.Id == tabPart);
                            if (chosen is null || (w.ActiveTabId == chosen.Id && s.FocusedViewId == chosen.Id)) { return (null, []); }
                            var updatedWins = s.Windows.Select(win => win.Id == winId ? win with { ActiveTabId = chosen.Id } : win).ToArray();
                            return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate([.. updatedWins.SelectMany(win => win.Tabs)], chosen.Id, s.Pins)
                            {
                                Windows = updatedWins,
                                Favourites = s.Favourites,
                            }, []);
                        });
                    }
                }
                else if (action == "newtab" || action.StartsWith("newtab:", StringComparison.Ordinal))
                {
                    var winId = action == "newtab" ? null : action["newtab:".Length..].Trim();
                    openedApp = "ino";
                    await ApplyDesktopWorkspaceChangeAsync(brain, signal.RequestId, s => OpenDesktopAppInState(brain, s, "ino", winId, asNewTab: true));
                }
                else if (action.StartsWith("pin:", StringComparison.Ordinal) || action.StartsWith("unpin:", StringComparison.Ordinal))
                {
                    var isPin = action.StartsWith("pin:", StringComparison.Ordinal);
                    var pinApp = (isPin ? action["pin:".Length..] : action["unpin:".Length..]).Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(pinApp) && pinApp != "ino" && pinApp != "desktop")
                    {
                        var appKey = IApp.Key(brain, pinApp);
                        await ApplyDesktopWorkspaceChangeAsync(brain, signal.RequestId, s =>
                        {
                            var alreadyPinned = s.Pins.Any(p => string.Equals(p.Id, pinApp, StringComparison.OrdinalIgnoreCase));
                            if (alreadyPinned == isPin) { return (null, []); }
                            IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef[] nextPins = isPin
                                ? [.. s.Pins, new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef(pinApp, appKey, "full", string.Empty)]
                                : [.. s.Pins.Where(p => !string.Equals(p.Id, pinApp, StringComparison.OrdinalIgnoreCase))];
                            return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate(s.OpenViews, s.FocusedViewId, nextPins)
                            {
                                Windows = s.Windows,
                                Favourites = s.Favourites,
                            }, []);
                        });
                    }
                }
                else if (action.StartsWith("favourite:", StringComparison.Ordinal))
                {
                    var winId = action["favourite:".Length..].Trim();
                    await ApplyDesktopWorkspaceChangeAsync(brain, signal.RequestId, s =>
                    {
                        var w = s.Windows.FirstOrDefault(win => win.Id == winId);
                        var activeTab = w?.Tabs.FirstOrDefault(t => t.Id == w.ActiveTabId) ?? w?.Tabs.LastOrDefault();
                        if (activeTab is null) { return (null, []); }
                        var appId = IApp.NameOf(brain, activeTab.App) ?? activeTab.Id;
                        var exists = s.Favourites.Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));
                        IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef[] nextFavs = exists
                            ? [.. s.Favourites.Where(f => !string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase))]
                            : [.. s.Favourites.Take(4), new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef(appId, activeTab.App, string.Empty, string.Empty)];
                        return (new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate(s.OpenViews, s.FocusedViewId, s.Pins)
                        {
                            Windows = s.Windows,
                            Favourites = nextFavs,
                        }, []);
                    });
                }
                else if (action.StartsWith("widget-open:", StringComparison.Ordinal) || action.StartsWith("widget-close:", StringComparison.Ordinal))
                {
                    var isOpen = action.StartsWith("widget-open:", StringComparison.Ordinal);
                    var widgetId = (isOpen ? action["widget-open:".Length..] : action["widget-close:".Length..]).Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(widgetId))
                    {
                        var widgetsNeuron = SiloNeurons.Get<IText>(services, $"{brain}/apps/desktop/widgets");
                        var currentCsv = (await widgetsNeuron.Read()).Markdown ?? string.Empty;
                        var list = currentCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        if (isOpen)
                        {
                            if (!list.Contains(widgetId, StringComparer.OrdinalIgnoreCase)) { list.Add(widgetId); }
                        }
                        else
                        {
                            list.RemoveAll(item => string.Equals(item, widgetId, StringComparison.OrdinalIgnoreCase));
                        }
                        await widgetsNeuron.Set(string.Join(",", list));
                    }
                }
                else
                {
                    forwardAction = true;
                }

                await EnsureDesktopViewAsync(brain);
            }
            finally
            {
                gate.Release();
            }

            if (openedApp is not null)
            {
                await EnsureOpenAppViewAsync(brain, openedApp, ct);
            }
            if (forwardAction)
            {
                await HandleViewActionAsync(brain, "desktop", signal, ct);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Desktop view action {Action} did not complete in {Brain}.", signal.Action, brain);
        }
    }

    private async Task EnsureAppAsync(string brain, string appName)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var appKey = IApp.Key(brain, appName);
            var gate = _gates.GetOrAdd(appKey, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try { await EnsureAssignedAsync(brain, appName, appKey, CancellationToken.None); }
            finally { gate.Release(); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Compiled app {App} was not initialized in {Brain}.", appName, brain);
        }
    }

    private async Task<DigitalBrain.Apps.Hosting.AppTurns?> EnsureAssignedAsync(string brain, string appName, string appKey, CancellationToken ct)
    {
        if (HostedByName.ContainsKey(appName))
        {
            var spec = ResolveSpec(appName);
            var app = SiloNeurons.Get<IApp>(services, appKey);
            var snapshot = await app.Read();
            if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
            {
                await app.Import(spec.Bundle, new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), spec.Name, spec.Bundle.Version));
            }
            else if (snapshot.Manifest.Operations.Length < spec.Bundle.Manifest.Operations.Length)
            {
                await app.Replace(spec.Bundle, new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), spec.Name, spec.Bundle.Version));
            }

            var turns = host.TryGetTurns(appKey);
            if (turns is null)
            {
                await host.AssignType(appKey, spec.AppType, new Dictionary<string, string>(StringComparer.Ordinal), ct);
                turns = host.TryGetTurns(appKey)!;
                WireInoServices(brain, turns.App);
                await AttachWatchesAsync(brain, appKey, turns);
            }
            return turns;
        }

        var existingTurns = host.TryGetTurns(appKey);
        if (existingTurns is not null)
        {
            return existingTurns;
        }

        var unhostedSnapshot = await SiloNeurons.Get<IApp>(services, appKey).Read();
        if (unhostedSnapshot.Settings.TryGetValue(InProcessAppRuntime.CompiledTypeKey, out var typeName)
            && !string.IsNullOrWhiteSpace(typeName))
        {
            var type = Type.GetType(typeName, throwOnError: false)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(typeName, throwOnError: false))
                    .FirstOrDefault(t => t is not null);
            if (type is not null)
            {
                await host.AssignType(appKey, type, new Dictionary<string, string>(unhostedSnapshot.Settings, StringComparer.Ordinal), ct);
                var assignedTurns = host.TryGetTurns(appKey);
                if (assignedTurns is not null)
                {
                    WireInoServices(brain, assignedTurns.App);
                    await AttachWatchesAsync(brain, appKey, assignedTurns);
                }
                return assignedTurns;
            }
        }
        return null;
    }

    // The app's declared watches, served for as long as the host runs; re-assigning the app replaces them (listeners are keyed per app and watch).
    private async Task AttachWatchesAsync(string brain, string appKey, DigitalBrain.Apps.Hosting.AppTurns turns)
    {
        if (turns.App.DeclaredWatches().Count == 0) { return; }
        _ = await HostedAppWatches.Attach(listeners, turns, new AppAddress(brain, appKey),
            (contract, id) => SiloNeurons.Get(services, contract, id),
            (error, watch) => logger.LogWarning(error, "Watch {Watch} of {App} failed.", watch, appKey));
    }

    private void WireInoServices(string brain, App instance)
    {
        var type = instance.GetType();
        type.GetProperty("FilesVfs")?.SetValue(instance, _vfsByBrain.GetOrAdd(brain, b => WorkspaceFilesService.ForBrain(services, b)));
    }

    private async Task HandleAppRequestedAsync(string brain, string appName, AppRequested request, CancellationToken ct)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            var baseCaller = await HostCaller.ForBrain(services, brain);
            if (baseCaller is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var appKey = IApp.Key(brain, appName);
            var gate = _gates.GetOrAdd(appKey, static _ => new SemaphoreSlim(1, 1));
            string? openedDesktopApp = null;
            await gate.WaitAsync(ct);
            try
            {
                if (appName == "desktop")
                {
                    await EnsureDesktopViewAsync(brain);
                }
                var turns = await EnsureAssignedAsync(brain, appName, appKey, ct);
                if (turns is null) { return; }
                if (turns.Definition.FindOperation(request.Operation) is { } method)
                {
                    await turns.Request(request.Operation, ParseArgument(method, request.Input), ct);
                    var appNeuron = SiloNeurons.Get<IApp>(services, appKey);
                    var currentSnapshot = await appNeuron.Read();
                    using var appScope = baseCaller is not null ? CallerContextStamper.StampScope(HostCaller.AsApp(baseCaller, appKey)) : null;
                    await appNeuron.SaveRuntimeState(
                        JsonSerializer.Serialize(new { request.Operation, request.RequestId }),
                        currentSnapshot.RuntimeStateRevision);
                }
                if (appName == "desktop")
                {
                    if (request.Operation == "open" && !string.IsNullOrWhiteSpace(request.Input))
                    {
                        openedDesktopApp = request.Input.Trim().ToLowerInvariant();
                        await ApplyDesktopWorkspaceChangeAsync(brain, request.RequestId, s => OpenDesktopAppInState(brain, s, openedDesktopApp));
                    }
                    await EnsureDesktopViewAsync(brain);
                }

                var title = HostedByName.TryGetValue(appName, out var spec) ? spec.Bundle.Manifest.Title : appName;
                await SiloNeurons.Get<IApp>(services, appKey).Progress("idle", $"{title}: {request.Operation}");
                if (appName == "ino" && request.Operation == "send" && turns.App is App<InoAppState> inoRuntime && inoRuntime.State.Turns is { Length: > 0 } inoTurns)
                {
                    await ForwardStudioPromptIfRoutedAsync(brain, inoTurns[^1], ct);
                }
                if (appName == "ino" && request.Operation == "open")
                {
                    try { await SiloNeurons.Get<IText>(services, IApp.Key(brain, "desktop") + "/greeted").Set("true"); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { logger.LogDebug(error, "Could not mark desktop greeted in {Brain}.", brain); }
                }
            }
            finally { gate.Release(); }

            if (openedDesktopApp is not null)
            {
                await EnsureOpenAppViewAsync(brain, openedDesktopApp, ct);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Compiled app {App} did not complete {Operation} in {Brain}.", appName, request.Operation, brain);
        }
    }

    private async Task HandleComposerSubmittedAsync(string brain, ComposerSubmitted signal, CancellationToken ct)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var appKey = IApp.Key(brain, "ino");
            var composer = SiloNeurons.Get<IComposer>(services, appKey + "/composer");
            var composerState = await composer.Read();
            var submission = composerState.Submissions.FirstOrDefault(item => item.Id == signal.RequestId);
            if (submission is null || string.IsNullOrWhiteSpace(submission.Text)) { return; }

            await composer.Settle(signal.RequestId);
            var shortText = submission.Text.Length <= 42 ? submission.Text : submission.Text[..42] + "…";
            await SiloNeurons.Get<IApp>(services, appKey).Progress("working", $"Thinking · Processing '{shortText}'…");
            InoTurn? latestTurn = null;
            var gate = _gates.GetOrAdd(appKey, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var turns = await EnsureAssignedAsync(brain, "ino", appKey, ct);
                if (turns is not null && turns.Definition.FindOperation("send") is { } sendMethod)
                {
                    await turns.Request("send", ParseArgument(sendMethod, submission.Text), ct);
                }
                await SiloNeurons.Get<IApp>(services, appKey).Progress("idle", "Ino: answered");
                if (turns?.App is App<InoAppState> ino && ino.State.Turns is { Length: > 0 } inoTurns) { latestTurn = inoTurns[^1]; }
            }
            finally { gate.Release(); }

            if (latestTurn is not null) { await ForwardStudioPromptIfRoutedAsync(brain, latestTurn, ct); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Ino composer submission {RequestId} failed in {Brain}.", signal.RequestId, brain);
        }
    }

    private async Task ForwardStudioPromptIfRoutedAsync(string brain, InoTurn latestTurn, CancellationToken ct)
    {
        if (latestTurn.StartsWithStudio("markdraw"))
        {
            var markdrawOp = latestTurn.RouteHint["routed:markdraw.".Length..];
            if (string.IsNullOrWhiteSpace(markdrawOp))
            {
                markdrawOp = "patch-elements";
            }
            await SiloNeurons.Get<IApp>(services, IApp.Key(brain, "markdraw")).Request(markdrawOp, latestTurn.UserMessage);
            return;
        }

        string? studioApp = latestTurn.RouteHint.StartsWith("routed:photocraft.", StringComparison.Ordinal)
            ? "photocraft"
            : latestTurn.StartsWithStudio("filmcraft")
                ? "filmcraft"
                : null;
        if (studioApp is null) { return; }
        if (HostedByName.ContainsKey(studioApp))
        {
            await HandleAppRequestedAsync(brain, studioApp, new AppRequested("edit", latestTurn.UserMessage, Guid.NewGuid()), ct);
        }
        else
        {
            await SiloNeurons.Get<IApp>(services, IApp.Key(brain, studioApp)).Request("edit", latestTurn.UserMessage);
        }
    }

    private Task HandleViewActionAsync(string brain, string appName, ViewAction signal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(signal.Action)) { return Task.CompletedTask; }
        var parts = signal.Action.Split(':', 2, StringSplitOptions.TrimEntries);
        return HandleAppRequestedAsync(brain, appName, new AppRequested(parts[0], parts.Length > 1 ? parts[1] : string.Empty, signal.RequestId), ct);
    }

    internal static object? ParseArgument(MethodInfo method, string input)
    {
        var parameter = method.GetParameters().FirstOrDefault(p => p.ParameterType != typeof(CancellationToken));
        if (parameter is null) { return null; }
        var targetType = parameter.ParameterType;
        if (targetType == typeof(string)) { return string.IsNullOrEmpty(input) ? null : Uri.UnescapeDataString(input); }
        if (string.IsNullOrWhiteSpace(input))
        {
            var emptyCtor = targetType.GetConstructor(Type.EmptyTypes);
            if (emptyCtor is not null) { return emptyCtor.Invoke(null); }
            var defaultCtor = targetType.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue));
            return defaultCtor?.Invoke([.. defaultCtor.GetParameters().Select(p => p.DefaultValue)]);
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}')) { return JsonSerializer.Deserialize(trimmed, targetType, Json); }

        var segs = trimmed.Split(':').Select(Uri.UnescapeDataString).ToArray();
        var unescaped = Uri.UnescapeDataString(trimmed);
        var unquoted = unescaped.Length >= 2 && unescaped[0] == '"' && unescaped[^1] == '"' ? unescaped[1..^1] : unescaped;

        var ctor = targetType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) { return null; }

        var ctorParams = ctor.GetParameters();
        if (ctorParams.Length == 0) { return ctor.Invoke(null); }

        if (ctorParams.Length == 1 || ShouldBindEntireInputToFirstParameter(ctorParams, segs))
        {
            var singleArgs = new object?[ctorParams.Length];
            singleArgs[0] = ConvertSegment(unquoted, ctorParams[0]);
            for (var i = 1; i < ctorParams.Length; i++)
            {
                singleArgs[i] = ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : GetDefaultForType(ctorParams[i].ParameterType);
            }
            return ctor.Invoke(singleArgs);
        }

        var args = new object?[ctorParams.Length];
        for (var i = 0; i < ctorParams.Length; i++)
        {
            args[i] = i < segs.Length
                ? ConvertSegment(segs[i], ctorParams[i])
                : (ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : GetDefaultForType(ctorParams[i].ParameterType));
        }
        return ctor.Invoke(args);
    }

    private static bool ShouldBindEntireInputToFirstParameter(ParameterInfo[] ctorParams, string[] segs)
    {
        if (segs.Length <= 1)
        {
            return true;
        }

        if (!ctorParams.Skip(1).All(p => p.HasDefaultValue))
        {
            return false;
        }

        var firstParamName = ctorParams[0].Name ?? string.Empty;
        if (firstParamName.Equals("Text", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Prompt", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Query", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Sql", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Content", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Sentence", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static object? ConvertSegment(string raw, ParameterInfo param)
    {
        var underlying = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
        if (underlying == typeof(string))
        {
            return raw;
        }
        if (underlying == typeof(bool))
        {
            return bool.TryParse(raw, out var b)
                ? b
                : (param.HasDefaultValue ? param.DefaultValue : true);
        }
        if (underlying == typeof(int))
        {
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n
                : (param.HasDefaultValue ? param.DefaultValue : 0);
        }
        if (underlying == typeof(long))
        {
            return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)
                ? l
                : (param.HasDefaultValue ? param.DefaultValue : 0L);
        }
        if (underlying == typeof(double))
        {
            return double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d
                : (param.HasDefaultValue ? param.DefaultValue : 0.0);
        }
        if (underlying.IsEnum && Enum.TryParse(underlying, raw, ignoreCase: true, out var enumVal))
        {
            return enumVal;
        }

        return param.HasDefaultValue ? param.DefaultValue : GetDefaultForType(param.ParameterType);
    }

    private static object? GetDefaultForType(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type) : null;
}

file static class InoTurnRoutingExtensions
{
    public static bool StartsWithStudio(this InoTurn turn, string studio)
        => turn.RouteHint.StartsWith($"routed:{studio}.", StringComparison.Ordinal);
}

internal sealed class CompiledAppsLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe<CompiledAppsLifecycle>(
            ServiceLifecycleStage.Active,
            cancellationToken => ActivatorUtilities.CreateInstance<CompiledAppsHost>(services).Execute(cancellationToken));
    }
}
