using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using IntoChat.OS.Modules.Apps.Hosting.InProcess;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

internal sealed class CompiledAppsHost(
    IServiceProvider services,
    InProcessAppHost host,
    DigitalBrain.Spi.IClusterListeners listeners,
    IEnumerable<CompiledAppsRegistration> registrations,
    ILogger<CompiledAppsHost> logger) : IStartupTask
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    // The shipped apps by name, found through the compiled-app registrations; the boot set is what the registrations name by type.
    internal static IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)> ShippedBy(IEnumerable<CompiledAppsRegistration> registrations)
        => registrations
            .SelectMany(registration => CompiledApps.Of(registration.Assembly))
            .GroupBy(compiled => compiled.Bundle.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (group.Key, group.First().AppType, group.First().Bundle), StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)> HostedBy(IEnumerable<CompiledAppsRegistration> registrations)
    {
        var shipped = ShippedBy(registrations);
        return registrations
            .SelectMany(registration => registration.Boot ?? [])
            .Select(type => type.GetCustomAttribute<DigitalBrain.AppAttribute>(false)?.Id)
            .OfType<string>()
            .Where(shipped.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => shipped[name], StringComparer.Ordinal);
    }

    private IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)> Shipped => _shipped ??= ShippedBy(registrations);

    private IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)> HostedByName => _hostedByName ??= HostedBy(registrations);

    private IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)>? _shipped;
    private IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)>? _hostedByName;

    private (string Name, Type AppType, AppBundle Bundle) ResolveSpec(string appName) => HostedByName[appName];

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
            if (appName == "desktop" && DesktopWindowVerbs.Contains(signal.Operation))
            {
                _ = Task.Run(() => HandleDesktopActionAsync(publisher.Brain.Value, signal, token), CancellationToken.None);
                return Task.CompletedTask;
            }
            if (appName == "desktop")
            {
                _ = Task.Run(() => EnsureDesktopViewAsync(publisher.Brain.Value), CancellationToken.None);
            }
            _ = Task.Run(() => HandleAppRequestedAsync(publisher.Brain.Value, appName, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        TriggerEnsureAll("owner");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAllAsync(string brain)
    {
        foreach (var name in HostedByName.Keys) { await EnsureAppAsync(brain, name); }
        await EnsureDesktopViewAsync(brain);
    }

    // The open widgets the desktop app keeps in its records, comma separated as the window shell shows them.
    private async Task<string> DesktopWidgetsAsync(string brain)
        => string.Join(",", (await SiloNeurons.Get<IDesktopRecords>(services, $"{brain}/{IDesktopRecords.Key}").Read()).Widgets);

    internal async Task EnsureDesktopViewAsync(string brain)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var workspace = SiloNeurons.Get<IntoChat.OS.Modules.Flutter.Workspace.IWorkspace>(services, IntoChat.OS.Modules.Flutter.Workspace.IWorkspace.Key(brain));
            var state = await workspace.Read();
            var widgetsText = await DesktopWidgetsAsync(brain);
            var manifests = await ShippedManifests.Of(services);

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

            DigitalBrain.UI.Node BuildWindowFrame(IntoChat.OS.Modules.Flutter.Workspace.WorkspaceWindow window)
            {
                var winId = window.Id;
                var activeIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
                var activeTab = window.Tabs[activeIndex];
                var appId = IApp.NameOf(brain, activeTab.App) ?? activeTab.Id;
                var title = manifests.Title(appId);
                var isPinned = state.Pins.Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(IApp.NameOf(brain, p.App), appId, StringComparison.OrdinalIgnoreCase));
                var isFav = state.Favourites.Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));

                DigitalBrain.UI.Node[] chromeButtons =
                [
                    DesktopButton($"focus-{winId}", title, "focus", winId),
                    DesktopIcon($"newtab-{winId}", "plus", "New tab", "newtab", winId),
                    DesktopIcon($"star-{winId}", "star", isFav ? "Remove from favourites" : "Add to favourites", "favourite", winId),
                    .. appId == "ino"
                        ? Array.Empty<DigitalBrain.UI.Node>()
                        : [isPinned
                            ? DesktopIcon($"unpin-{winId}", "pin-off", "Unpin", "unpin", appId)
                            : DesktopIcon($"pin-{winId}", "pin", "Pin", "pin", appId)],
                    DesktopIcon($"close-{winId}", "x", "Close tab", "close", $"{winId}/{activeIndex}"),
                ];
                var tabs = window.Tabs.Select((tab, idx) => new DigitalBrain.UI.TabItem(
                    $"tab-{winId}-{idx}", $"{winId}/{idx}", manifests.Title(IApp.NameOf(brain, tab.App) ?? tab.Id), DesktopEmit("tab", $"{winId}/{idx}"))).ToArray();

                return new DigitalBrain.UI.Column(
                [
                    new DigitalBrain.UI.Row(chromeButtons) { Gap = 4, Extents = [0, .. chromeButtons.Skip(1).Select(_ => 32.0)] },
                    new DigitalBrain.UI.Tabs(tabs, $"{winId}/{activeIndex}"),
                    new DigitalBrain.UI.NeuronRef(UIVocabulary.ViewportType, $"{brain}/apps/desktop/window/{winId}"),
                ]) { Gap = 0, Extents = [40, 38, 0] };
            }

            DigitalBrain.UI.Node windows = activeWindows.Length == 0
                ? new DigitalBrain.UI.Column([])
                : new DigitalBrain.UI.Column([.. activeWindows.Chunk(3).Select(row => (DigitalBrain.UI.Node)new DigitalBrain.UI.Row([.. row.Select(BuildWindowFrame)]))])
                {
                    Extents = [.. activeWindows.Chunk(3).Select(_ => 0.0)],
                };

            var dockApps = manifests.DockApps();
            var dock = new DigitalBrain.UI.Row(
            [
                new DigitalBrain.UI.Row(
                [
                    .. dockApps.Select(app => (DigitalBrain.UI.Node)new DigitalBrain.UI.Button($"dock-{app}", manifests.Title(app), DesktopEmit("open", app), Tone: app == "ino" ? DigitalBrain.UI.ButtonTone.Accent : DigitalBrain.UI.ButtonTone.Neutral) { Icon = app == "ino" ? "ino" : null }),
                    DesktopIcon("newtab", "plus", "New tab", "newtab", null),
                ]) { Gap = 4 },
                new DigitalBrain.UI.Row(
                [
                    new DigitalBrain.UI.Text("Usage"),
                    new DigitalBrain.UI.Text(widgetsText),
                    DesktopButton("avatar", "You", "open", "friends"),
                    DesktopButton("more-settings", "Settings", "settings", null),
                ]) { Gap = 4 },
            ]) { Extents = [0, 360] };

            var next = new DigitalBrain.UI.View("Desktop", new DigitalBrain.UI.Column([windows, dock]) { Extents = [0, 68] });
            using (HostCaller.AsPlatform(brain)) { await SiloNeurons.Get<IView>(services, $"{brain}/apps/desktop/view").Publish(1, next); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Initial desktop view seed for {Brain} did not complete.", brain);
        }
    }

    internal async Task EnsureOpenAppViewAsync(string brain, string appName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appName) || appName is "desktop" or "workspace") { return; }
        try
        {
            var baseCaller = await HostCaller.ForBrain(services, brain);
            if (baseCaller is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            if (appName == IntoChat.Files.FilesApp.AppName)
            {
                var filesView = await SiloNeurons.Get<IView>(services, $"{brain}/apps/files/view").Read();
                if (filesView is null)
                {
                    using var filesScope = baseCaller is not null ? CallerContextStamper.StampScope(HostCaller.AsApp(baseCaller, IApp.Key(brain, IntoChat.Files.FilesApp.AppName))) : null;
                    await SiloNeurons.Get<IFilesWorkspace>(services, $"{brain}/apps/files/workspace").Open(ct);
                }
                return;
            }

            var view = await SiloNeurons.Get<IView>(services, $"{brain}/apps/{appName}/view").Read();
            if (view is null)
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
            if (!Shipped.TryGetValue("desktop", out var shipped)) { return; }
            var bundle = shipped.Bundle;
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

    private async Task HandleDesktopActionAsync(string brain, AppRequested signal, CancellationToken ct)
    {
        var argument = TextOf(signal.Input);
        var desktopAction = argument.Length == 0 ? signal.Operation : signal.Operation + ":" + argument;
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
                var action = desktopAction.Trim();
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
                        // The desktop app keeps its widgets in its own state.
                        if (await EnsureAssignedAsync(brain, "desktop", desktopKey, ct) is { } desktopTurns
                            && desktopTurns.Definition.FindOperation(isOpen ? "widget-open" : "widget-close") is { } widgetMethod)
                        { await desktopTurns.Request(isOpen ? "widget-open" : "widget-close", BindArgument(widgetMethod, widgetId), ct); }
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
                await HandleAppRequestedAsync(brain, "desktop", signal, ct);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Desktop view action {Action} did not complete in {Brain}.", desktopAction, brain);
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
                WireVfsService(brain, turns.App);
            }
            await AttachWatchesAsync(brain, appKey, turns);
            return turns;
        }

        var existingTurns = host.TryGetTurns(appKey);
        if (existingTurns is not null)
        {
            await AttachWatchesAsync(brain, appKey, existingTurns);
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
                    WireVfsService(brain, assignedTurns.App);
                    await AttachWatchesAsync(brain, appKey, assignedTurns);
                }
                return assignedTurns;
            }
        }
        return null;
    }

    // Each assigned app's watch subscriptions, by app key, with the turns they feed: a reassigned app (new turns) gets new ones and
    // the old ones are disposed; a retired app's are disposed on its next ensure.
    private readonly ConcurrentDictionary<string, (DigitalBrain.Apps.Hosting.AppTurns Turns, IAsyncDisposable Watches)> _watches = new(StringComparer.Ordinal);

    internal async Task AttachWatchesAsync(string brain, string appKey, DigitalBrain.Apps.Hosting.AppTurns? turns)
    {
        if (_watches.TryGetValue(appKey, out var attached) && ReferenceEquals(attached.Turns, turns)) { return; }
        if (attached.Watches is not null && _watches.TryRemove(new(appKey, attached)))
        {
            try { await attached.Watches.DisposeAsync(); }
            catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Watches of {App} did not stop cleanly.", appKey); }
        }
        if (turns is null || turns.App.DeclaredWatches().Count == 0) { return; }
        if (services.GetService<IIntoChatOSBrains>() is not { } brains)
        {
            logger.LogWarning("App {App} declares watches but this host has no brain client; its watches are not served.", appKey);
            return;
        }
        var watches = HostedAppWatches.Attach(brains.Open(brain), turns, new AppAddress(brain, appKey),
            (contract, id) => SiloNeurons.Get(services, contract, id),
            (error, watch) => logger.LogWarning(error, "Watch {Watch} of {App} failed.", watch, appKey),
            (watch, attempt, delay) => logger.LogInformation("Watch {Watch} of {App} restarts in {Delay} (attempt {Attempt}).", watch, appKey, delay, attempt));
        if (!_watches.TryAdd(appKey, (turns, watches))) { await watches.DisposeAsync(); }
    }

    // Test seam: ensure one app as the host does on activation and return its turns.
    internal async Task<DigitalBrain.Apps.Hosting.AppTurns?> EnsureForTestAsync(string brain, string appName, CancellationToken ct)
    {
        var turns = await EnsureAssignedAsync(brain, appName, IApp.Key(brain, appName), ct);
        return turns;
    }

    private void WireVfsService(string brain, App instance)
    {
        if (instance is IWithFilesVfs vfsAware)
        {
            vfsAware.FilesVfs = _vfsByBrain.GetOrAdd(brain, b => WorkspaceFilesService.ForBrain(services, b));
        }
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
                    await turns.Request(request.Operation, BindArgument(method, request.Input), ct);
                }
                if (appName == "desktop")
                {
                    if (request.Operation == "open" && !string.IsNullOrWhiteSpace(TextOf(request.Input)))
                    {
                        openedDesktopApp = TextOf(request.Input).ToLowerInvariant();
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

    internal async Task ForwardStudioPromptIfRoutedAsync(string brain, InoTurn latestTurn, CancellationToken ct)
    {
        var studioApp = (await ShippedManifests.Of(services)).StudioFor(latestTurn.RouteHint);
        if (studioApp is null) { return; }
        if (HostedByName.ContainsKey(studioApp))
        {
            await HandleAppRequestedAsync(brain, studioApp, new AppRequested(ShippedManifests.StudioOperation, latestTurn.UserMessage, Guid.NewGuid()), ct);
        }
        else
        {
            await SiloNeurons.Get<IApp>(services, IApp.Key(brain, studioApp)).Request(ShippedManifests.StudioOperation, latestTurn.UserMessage);
        }
    }

    // The desktop's window verbs: the host's shell handles them; any other desktop operation is the desktop app's.
    private static readonly HashSet<string> DesktopWindowVerbs = new(StringComparer.Ordinal)
    { "close", "close-app", "focus", "tab", "newtab", "pin", "unpin", "favourite", "widget-open", "widget-close", "settings" };

    private static DigitalBrain.UI.Emit DesktopEmit(string signal, string? argument)
        => new("desktop", signal, argument is null ? null : JsonSerializer.SerializeToElement(argument));

    private static DigitalBrain.UI.Button DesktopButton(string key, string label, string signal, string? argument)
        => new(key, label, DesktopEmit(signal, argument));

    private static DigitalBrain.UI.Button DesktopIcon(string key, string icon, string label, string signal, string? argument)
        => new(key, label, DesktopEmit(signal, argument)) { Icon = icon, Small = true };

    // A request's input as text: a JSON string's value (a view emit's single argument), otherwise the input itself.
    internal static string TextOf(string input)
    {
        if (TryJson(input) is { ValueKind: JsonValueKind.String } text) { return text.GetString() ?? ""; }
        return input.Trim();
    }

    private static JsonElement? TryJson(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0 || trimmed[0] is not ('"' or '[' or '{')) { return null; }
        try { using var document = JsonDocument.Parse(trimmed); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    // Binds an operation's argument: from a view emit's args (JSON: a string, an array bound positionally, an object by name),
    // or from plain text (a person's sentence, an HTTP request), which fills the record's first member.
    internal static object? BindArgument(MethodInfo method, string input)
    {
        var parameter = method.GetParameters().FirstOrDefault(p => p.ParameterType != typeof(CancellationToken));
        if (parameter is null) { return null; }
        var targetType = parameter.ParameterType;
        var json = TryJson(input);
        if (targetType == typeof(string))
        {
            var text = json is { ValueKind: JsonValueKind.String } str ? str.GetString() : input;
            return string.IsNullOrEmpty(text) ? null : text;
        }
        if (string.IsNullOrWhiteSpace(input))
        {
            var emptyCtor = targetType.GetConstructor(Type.EmptyTypes);
            if (emptyCtor is not null) { return emptyCtor.Invoke(null); }
            var defaultCtor = targetType.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue));
            return defaultCtor?.Invoke([.. defaultCtor.GetParameters().Select(p => p.DefaultValue)]);
        }
        if (json is { ValueKind: JsonValueKind.Object } obj) { return obj.Deserialize(targetType, Json); }

        var ctor = targetType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) { return null; }
        var ctorParams = ctor.GetParameters();
        if (ctorParams.Length == 0) { return ctor.Invoke(null); }
        static object? Default(ParameterInfo p) => p.HasDefaultValue ? p.DefaultValue : GetDefaultForType(p.ParameterType);

        var args = new object?[ctorParams.Length];
        if (json is { ValueKind: JsonValueKind.Array } array)
        {
            var items = array.EnumerateArray().ToArray();
            for (var i = 0; i < ctorParams.Length; i++)
            {
                args[i] = i >= items.Length ? Default(ctorParams[i])
                    : items[i].ValueKind == JsonValueKind.String ? ConvertSegment(items[i].GetString() ?? "", ctorParams[i])
                    : items[i].Deserialize(ctorParams[i].ParameterType, Json);
            }
            return ctor.Invoke(args);
        }

        args[0] = ConvertSegment(json is { ValueKind: JsonValueKind.String } single ? single.GetString() ?? "" : input.Trim(), ctorParams[0]);
        for (var i = 1; i < ctorParams.Length; i++) { args[i] = Default(ctorParams[i]); }
        return ctor.Invoke(args);
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
