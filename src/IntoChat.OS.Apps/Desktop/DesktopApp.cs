/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow, auto-arrange, pin, close, close-app, focus, tab, newtab, unpin, favourite, widget-open, widget-close, settings

The embedded folder scripts publish the Flutter desktop. These operations present Smart Desktop state, Whiteboard sticky-note app pinning, auto-arrange, and 3D BrainScene telemetry.
*/
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Modules.Flutter.Workspace.Signals;
using DigitalBrain.Log;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Apps.Desktop;

[Signal("desktop.start")]
public sealed record DesktopStart : Signal;

[Signal("desktop.layout")]
public sealed record DesktopLayoutChoice : Signal;

[Signal("desktop.open")]
public sealed record DesktopOpen(string? App = null, string? View = null) : Signal;

[Signal("desktop.close")]
public sealed record DesktopClose(string? Target = null) : Signal;

[Signal("desktop.close-app")]
public sealed record DesktopCloseApp(string? App = null) : Signal;

[Signal("desktop.focus")]
public sealed record DesktopFocus(string? WindowId = null) : Signal;

[Signal("desktop.tab")]
public sealed record DesktopTab(string? Target = null) : Signal;

[Signal("desktop.newtab")]
public sealed record DesktopNewTab(string? WindowId = null) : Signal;

[Signal("desktop.narrow")]
public sealed record DesktopNarrow(string? Narrow = null) : Signal;

[Signal("desktop.auto-arrange")]
public sealed record DesktopAutoArrange : Signal;

[Signal("desktop.pin")]
public sealed record DesktopPinApp(string? AppId = null, bool Pinned = true) : Signal;

[Signal("desktop.unpin")]
public sealed record DesktopUnpinApp(string? AppId = null) : Signal;

[Signal("desktop.favourite")]
public sealed record DesktopFavourite(string? WindowId = null) : Signal;

[Signal("desktop.widget-open")]
public sealed record DesktopWidgetOpen(string? Widget = null) : Signal;

[Signal("desktop.widget-close")]
public sealed record DesktopWidgetClose(string? Widget = null) : Signal;

[Signal("desktop.settings")]
public sealed record DesktopSettings : Signal;

public sealed record DesktopAppState(
    string LayoutMode,
    bool IsNarrow,
    string StylePreset,
    string BarLayout,
    int ActiveNeurons,
    string Status,
    string[] PinnedApps,
    WorkspaceWindow[] Windows,
    string? FocusedViewId,
    WorkspaceViewRef[] Pins,
    WorkspaceViewRef[] Favourites,
    string WidgetsText)
{
    public DesktopAppState() : this(
        "smart-desktop",
        false,
        "fluent-acrylic",
        "floating-glass",
        10,
        "Smart Desktop Ready",
        ["ino", "creator"],
        [],
        null,
        [],
        [],
        "")
    { }
}

[Neuron("app.desktop")]
public sealed class DesktopApp : Screen<DesktopAppState>
{
    public const string AppName = "desktop";

    public static readonly HashSet<string> DesktopWindowVerbs = new(StringComparer.Ordinal)
    { "close", "close-app", "focus", "tab", "newtab", "pin", "unpin", "favourite", "widget-open", "widget-close", "settings" };

    public static AppBundle Bundle { get; } = new(
        "desktop",
        "0.4.0",
        new AppManifest(
            Title: "Desktop",
            Description: "Your apps in windows, with a dock.",
            Operations:
            [
                new("start", "Compose the desktop from the open windows and installed apps."),
                new("layout", "Choose the layout the next connect shows: desktop or classic."),
                new("open", "Open an app's window beside the others, or focus it (input: the app name). Enforces the window cap."),
                new("narrow", "The shell's width hint: true below 900 logical pixels, false otherwise."),
                new("auto-arrange", "Arrange the open windows."),
                new("focus", "Focus the window named by Input."),
                new("tab", "Activate the tab {window}/{index} named by Input."),
                new("newtab", "Open a new tab, in the window named by Input when given."),
                new("close", "Close the tab {window}/{index} named by Input."),
                new("close-app", "Close the app named by Input's windows."),
                new("pin", "Pin the app named by Input to the dock."),
                new("unpin", "Unpin the app named by Input from the dock."),
                new("favourite", "Toggle the favourite of the window named by Input's active tab."),
                new("widget-open", "Open the widget named by Input."),
                new("widget-close", "Close the widget named by Input."),
                new("settings", "Open the settings."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(DesktopApp).AssemblyQualifiedName,
    };

    private IDesktopRecords Records => Get<IDesktopRecords>(IDesktopRecords.Key);

    public static readonly IReadOnlyList<(string Id, string Title, string Uri, string Icon, string Category, string RuntimeMode)> NativeCatalog =
    [
        ("ino", "Ino Assistant", "neuron://local/apps/ino", "sparkles", "Intelligence", "orleans-grain"),
        ("files", "Files", "neuron://local/apps/files", "folder", "Workspace", "orleans-grain"),
        ("workspace", "Workspace", "neuron://local/apps/workspace", "compass", "Workspace", "orleans-grain"),
        ("tasks", "Tasks", "neuron://local/apps/tasks", "tasks", "Workspace", "orleans-grain"),
        ("data", "Data Studio", "neuron://local/apps/data", "bar-chart-2", "Studio", "orleans-grain"),
        ("markdraw", "Markdraw", "neuron://local/apps/markdraw", "pen-tool", "Studio", "orleans-grain"),
        ("creator", "Creator 3D Loop", "neuron://local/apps/creator", "cpu", "Studio", "orleans-grain"),
        ("gallery", "UI Gallery", "neuron://local/apps/gallery", "layout-grid", "Studio", "orleans-grain"),
        ("friends", "Friends", "neuron://local/apps/friends", "users", "Social", "orleans-grain"),
        ("usage", "Usage", "neuron://local/apps/usage", "wallet", "System", "orleans-grain"),
        ("settings", "Settings", "neuron://local/apps/settings", "settings", "System", "orleans-grain"),
    ];

    public static IReadOnlyList<(string Id, string Title, string Uri, string Icon, string Category, string RuntimeMode)> Catalog { get; } =
        NativeCatalog.Concat(
            ArtCraftCatalog.All.Select(app => (app.Id, app.Title, $"neuron://local/apps/{app.Id}", app.Icon, app.Category, "wasm-ui+mcp+cli"))
        ).ToArray();

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        try
        {
            await Follow<IWorkspace, WorkspaceChanged>("main");
        }
        catch (Exception ex)
        {
            NullLogger.Instance.LogSwallowed(ex, "Workspace follow is optional when running in standalone mode.");
        }
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public async Task HandleWorkspaceChanged(WorkspaceChanged _)
    {
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public async Task Start(DesktopStart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var layout = "smart-desktop";
        try
        {
            var records = await Records.Read();
            if (records.Layout is { Length: > 0 }) { layout = records.Layout; }
            if (!records.Greeted)
            {
                try
                {
                    try { await Get<IntoChat.OS.Modules.Apps.IApp>("apps/ino").Request("open", "greet"); }
                    catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException) { await Get<IScreen>("app.ino").Request("open", System.Text.Json.JsonSerializer.SerializeToElement("greet")); }
                }
                catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "Ino was not there to greet; the desktop starts without it."); }
                await Records.Write(records with { Greeted = true });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
        }
        await SyncSmartDesktopAsync(layout, State.IsNarrow);
    }

    [On]
    public async Task WidgetOpen(DesktopWidgetOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var records = await Records.Read();
            if (WidgetId(request.Widget) is { } id && !records.Widgets.Contains(id, StringComparer.Ordinal))
            {
                await Records.Write(records with { Widgets = [.. records.Widgets, id] });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
        }
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public async Task WidgetClose(DesktopWidgetClose request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var records = await Records.Read();
            if (WidgetId(request.Widget) is { } id && records.Widgets.Contains(id, StringComparer.Ordinal))
            {
                await Records.Write(records with { Widgets = [.. records.Widgets.Where(known => known != id)] });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
        }
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    private static string? WidgetId(string? widget) => string.IsNullOrWhiteSpace(widget) ? null : widget.Trim().ToLowerInvariant();

    [On]
    public async Task Layout(DesktopLayoutChoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextLayout = State.LayoutMode == "smart-desktop" ? "spatial-grid" : "smart-desktop";
        try
        {
            await Records.Write(await Records.Read() with { Layout = nextLayout });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
        }
        await SyncSmartDesktopAsync(nextLayout, State.IsNarrow);
    }

    private sealed record DesktopOpenViewArgs(string Input, string View, string Section);

    [On]
    public async Task Open(DesktopOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var app = string.IsNullOrWhiteSpace(request.App) ? null : request.App.Trim().ToLowerInvariant();
        if (app is not null)
        {
            if (request.View is { } view)
            {
                try
                {
                    try { await Get<IntoChat.OS.Modules.Apps.IApp>($"apps/{app}").Request("open", view.Trim()); }
                    catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException) { await Get<IScreen>("app." + app).Request("open", System.Text.Json.JsonSerializer.SerializeToElement(new DesktopOpenViewArgs(view.Trim(), view.Trim(), view.Trim()))); }
                }
                catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "The app was not there to receive its open view."); }
            }
            await ApplyDesktopWorkspaceChangeAsync(s => OpenDesktopAppInState(s, app));
        }
        else
        {
            await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
        }
    }

    [On]
    public Task Narrow(DesktopNarrow request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SyncSmartDesktopAsync(State.LayoutMode, string.Equals(request.Narrow, "true", StringComparison.OrdinalIgnoreCase));
    }

    [On]
    public async Task AutoArrange(DesktopAutoArrange request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pinnedList = State.PinnedApps is { Length: > 0 } ? State.PinnedApps : ["ino", "creator"];
        await Show(State with
        {
            LayoutMode = "whiteboard-auto-arranged",
            PinnedApps = pinnedList,
            Status = $"Whiteboard Auto-Arranged · {pinnedList.Length} pinned sticky-note app(s) locked on top ({string.Join(", ", pinnedList)})",
        });
    }

    [On]
    public async Task Pin(DesktopPinApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appId = string.IsNullOrWhiteSpace(request.AppId) ? "photocraft" : request.AppId.Trim().ToLowerInvariant();
        if (appId is "ino" or "desktop") { return; }
        var isPin = request.Pinned;
        var existingPins = State.PinnedApps is { Length: > 0 } ? State.PinnedApps : ["ino", "creator"];
        var nextPinnedApps = isPin
            ? (existingPins.Contains(appId, StringComparer.OrdinalIgnoreCase) ? existingPins : [.. existingPins, appId])
            : existingPins.Where(p => !string.Equals(p, appId, StringComparison.OrdinalIgnoreCase)).ToArray();
        await Show(State with { PinnedApps = nextPinnedApps });
        await ApplyDesktopWorkspaceChangeAsync(s =>
        {
            if (s.Pins.Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase)) == isPin) { return (null, []); }
            var appKey = IntoChat.OS.Modules.Apps.IApp.Key("local", appId);
            WorkspaceViewRef[] nextPins = isPin
                ? [.. s.Pins, new WorkspaceViewRef(appId, appKey, "full", string.Empty)]
                : [.. s.Pins.Where(p => !string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase))];
            return (BuildWorkspaceUpdate(s, s.Windows, pins: nextPins), []);
        });
    }

    [On]
    public async Task Unpin(DesktopUnpinApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.AppId))
        {
            await Pin(new DesktopPinApp(request.AppId, Pinned: false));
        }
    }

    [On]
    public async Task Close(DesktopClose request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Target)) { return; }
        await ApplyDesktopWorkspaceChangeAsync(s => CloseDesktopTargetInState(s, request.Target));
    }

    [On]
    public async Task CloseApp(DesktopCloseApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.App)) { return; }
        await ApplyDesktopWorkspaceChangeAsync(s => CloseDesktopAppInState(s, request.App));
    }

    [On]
    public async Task Focus(DesktopFocus request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WindowId)) { return; }
        await ApplyDesktopWorkspaceChangeAsync(s =>
            s.Windows.FirstOrDefault(win => win.Id == request.WindowId) is { } w && s.FocusedViewId != w.ActiveTabId
                ? (BuildWorkspaceUpdate(s, s.Windows, w.ActiveTabId), [])
                : (null, []));
    }

    [On]
    public async Task Tab(DesktopTab request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Target)) { return; }
        var target = request.Target.Trim();
        var sep = target.LastIndexOf('/');
        if (sep < 0) { sep = target.LastIndexOf(':'); }
        if (sep > 0)
        {
            var winId = target[..sep];
            var tabPart = target[(sep + 1)..];
            await ApplyDesktopWorkspaceChangeAsync(s =>
            {
                var w = s.Windows.FirstOrDefault(win => win.Id == winId);
                if (w is null) { return (null, []); }
                var chosen = int.TryParse(tabPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < w.Tabs.Length
                    ? w.Tabs[idx]
                    : w.Tabs.FirstOrDefault(t => t.Id == tabPart);
                if (chosen is null || (w.ActiveTabId == chosen.Id && s.FocusedViewId == chosen.Id)) { return (null, []); }
                var updatedWins = s.Windows.Select(win => win.Id == winId ? win with { ActiveTabId = chosen.Id } : win).ToArray();
                return (BuildWorkspaceUpdate(s, updatedWins, chosen.Id), []);
            });
        }
    }

    [On]
    public async Task NewTab(DesktopNewTab request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ApplyDesktopWorkspaceChangeAsync(s => OpenDesktopAppInState(s, "ino", request.WindowId, asNewTab: true));
    }

    [On]
    public async Task Favourite(DesktopFavourite request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WindowId)) { return; }
        await ApplyDesktopWorkspaceChangeAsync(s =>
        {
            var w = s.Windows.FirstOrDefault(win => win.Id == request.WindowId);
            var activeTab = w?.Tabs.FirstOrDefault(t => t.Id == w.ActiveTabId) ?? w?.Tabs.LastOrDefault();
            if (activeTab is null) { return (null, []); }
            var appId = IntoChat.OS.Modules.Apps.IApp.NameOf("local", activeTab.App) ?? activeTab.Id;
            var exists = s.Favourites.Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));
            WorkspaceViewRef[] nextFavs = exists
                ? [.. s.Favourites.Where(f => !string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase))]
                : [.. s.Favourites.Take(4), new WorkspaceViewRef(appId, activeTab.App, string.Empty, string.Empty)];
            return (BuildWorkspaceUpdate(s, s.Windows, favourites: nextFavs), []);
        });
    }

    [On]
    public async Task Settings(DesktopSettings request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Open(new DesktopOpen("settings"));
    }

    public new DesktopAppState State => base.State;
    public View Render() => Render(State);
    public Exception? RecordedError => null;

    private static new Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
        return new Emit("app.desktop", alias, JsonSerializer.SerializeToElement(signal));
    }
    protected override View Render(DesktopAppState state)
    {
        string brain;
        try { brain = Id.Brain.Value; } catch (InvalidOperationException ex) when (ex is not null) { NullLogger.Instance.LogSwallowed(ex, "Id.Brain is unassigned in standalone tests; defaulting to local."); brain = "local"; }
        if (string.IsNullOrEmpty(brain)) { brain = "local"; }
        var activeWindows = (state.Windows ?? []).Where(w => w.Tabs is { Length: > 0 }).ToArray();

        Node BuildWindowFrame(WorkspaceWindow window)
        {
            var winId = window.Id;
            var activeIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
            var activeTab = window.Tabs[activeIndex];
            var appId = IntoChat.OS.Modules.Apps.IApp.NameOf(brain, activeTab.App) ?? activeTab.Id;
            var title = Char.ToUpperInvariant(appId[0]) + appId[1..];
            var isPinned = (state.Pins ?? []).Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase));
            var isFav = (state.Favourites ?? []).Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));

            Node[] chromeButtons =
            [
                new Button($"focus-{winId}", title, Emit: Emits(new DesktopFocus(winId))),
                new Button($"newtab-{winId}", "New tab", Emit: Emits(new DesktopNewTab(winId))) { Icon = "plus", Small = true },
                new Button($"star-{winId}", isFav ? "Remove from favourites" : "Add to favourites", Emit: Emits(new DesktopFavourite(winId))) { Icon = "star", Small = true },
                .. appId == "ino"
                    ? Array.Empty<Node>()
                    : [isPinned
                        ? new Button($"unpin-{winId}", "Unpin", Emit: Emits(new DesktopUnpinApp(appId))) { Icon = "pin-off", Small = true }
                        : new Button($"pin-{winId}", "Pin", Emit: Emits(new DesktopPinApp(appId, true))) { Icon = "pin", Small = true }],
                new Button($"close-{winId}", "Close tab", Emit: Emits(new DesktopClose($"{winId}/{activeIndex}"))) { Icon = "x", Small = true },
            ];
            var tabs = window.Tabs.Select((tab, idx) => new TabItem(
                $"tab-{winId}-{idx}", $"{winId}/{idx}", Char.ToUpperInvariant(tab.Id[0]) + tab.Id[1..], Emits(new DesktopTab($"{winId}/{idx}")))).ToArray();

            return new Column(
            [
                new Row(chromeButtons) { Gap = 4, Extents = [0, .. chromeButtons.Skip(1).Select(_ => 32.0)] },
                new Tabs(tabs, $"{winId}/{activeIndex}"),
                new NeuronRef("viewport", $"{brain}/apps/desktop/window/{winId}"),
            ])
            { Gap = 0, Extents = [40, 38, 0] };
        }

        Node windows = activeWindows.Length == 0
            ? new Column([])
            : new Column([.. activeWindows.Chunk(3).Select(row => (Node)new Row([.. row.Select(BuildWindowFrame)]))])
            {
                Extents = [.. activeWindows.Chunk(3).Select(_ => 0.0)],
            };

        var dock = new Row(
        [
            new Row(
            [
                new Button("dock-ino", "Ino", Emit: Emits(new DesktopOpen("ino")), Tone: ButtonTone.Accent) { Icon = "ino" },
                new Button("dock-files", "Files", Emit: Emits(new DesktopOpen("files"))),
                new Button("dock-settings", "Settings", Emit: Emits(new DesktopSettings())),
                new Button("newtab", "New tab", Emit: Emits(new DesktopNewTab())) { Icon = "plus", Small = true },
            ]) { Gap = 4 },
            new Row(
            [
                new Text("Usage"),
                new Text(state.WidgetsText),
                new Button("avatar", "You", Emit: Emits(new DesktopOpen("friends"))),
                new Button("more-settings", "Settings", Emit: Emits(new DesktopSettings())),
            ]) { Gap = 4 },
        ])
        { Extents = [0, 360] };

        return new View("Desktop", new Column([windows, dock]) { Extents = [0, 68] });
    }

    private async Task SyncSmartDesktopAsync(string layoutMode, bool narrow)
    {
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetOptional<IUserSettings>("settings") is { } settings)
        {
            try
            {
                var current = await settings.Read();
                preset = current.StylePreset;
                barLayout = current.BarLayout;
            }
            catch (Exception ex) { NullLogger.Instance.LogSwallowed(ex, "UserSettings is optional."); }
        }


        var activeNeurons = 10;
        try
        {
            var topology = await Get<IntoChat.OS.Modules.Apps.Debugger.IDebuggerReads>("creator/reads").Topology();
            if (topology.Nodes.Count > 0) { activeNeurons = topology.Nodes.Count; }
        }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "DebuggerReads is optional."); }

        try
        {
            var units = await Get<ISynapseStudio>("ino/synapses").List();
            if (units.Length > 0 && activeNeurons == 10) { activeNeurons += units.Length; }
        }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "SynapseStudio is optional."); }

        var windows = State.Windows ?? [];
        var pins = State.Pins ?? [];
        var favs = State.Favourites ?? [];
        var focusedViewId = State.FocusedViewId;
        try
        {
            var ws = await Get<IWorkspace>("workspace").Read();
            windows = ws.Windows;
            pins = ws.Pins;
            favs = ws.Favourites;
            focusedViewId = ws.FocusedViewId;
        }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "Workspace is optional."); }

        var widgetsText = "";
        try
        {
            var records = await Records.Read();
            widgetsText = string.Join(" · ", records.Widgets);
        }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "Records is optional."); }

        var pinnedApps = pins.Length > 0
            ? pins.Select(p => p.Id).Where(id => id is not ("ino" or "desktop")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : (State.PinnedApps ?? ["ino", "creator"]);

        await Show(State with
        {
            LayoutMode = layoutMode,
            IsNarrow = narrow,
            StylePreset = preset,
            BarLayout = barLayout,
            ActiveNeurons = activeNeurons,
            Status = narrow ? "Smart Desktop (Compact)" : $"Smart Desktop ({layoutMode})",
            Windows = windows,
            FocusedViewId = focusedViewId,
            Pins = pins,
            Favourites = favs,
            PinnedApps = pinnedApps,
            WidgetsText = widgetsText,
        });
    }

    private async Task ApplyDesktopWorkspaceChangeAsync(
        Func<WorkspaceState, (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds)> change)
    {
        IReadOnlyList<string> closedWindowIds = [];
        try
        {
            var workspace = Get<IWorkspace>("workspace");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var wsState = await workspace.Read();
                var (update, closed) = change(wsState);
                closedWindowIds = closed;
                if (update is null) { break; }
                try
                {
                    await workspace.Apply(update, $"desktop:{Guid.NewGuid():N}:{attempt}", wsState.Revision);
                    break;
                }
                catch (RevisionConflictException ex) when (attempt == 0)
                {
                    NullLogger.Instance.LogSwallowed(ex, "Workspace revision conflict on initial attempt; retrying.");
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "Workspace is optional.");
        }

        await ClearViewportsAsync(closedWindowIds);
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    private async Task ClearViewportsAsync(IReadOnlyList<string> closedWindowIds)
    {
        var brain = Id.Brain.Value is { Length: > 0 } b ? b : "local";
        foreach (var winId in closedWindowIds)
        {
            try
            {
                var viewport = Get<IViewport>($"{brain}/apps/desktop/window/{winId}");
                var currentVp = await viewport.Read();
                if (currentVp.Target is not null)
                {
                    await viewport.Set(null, currentVp.Revision);
                }
            }
            catch (Exception ex)
            {
                NullLogger.Instance.LogSwallowed(ex, "Failed to clear viewport for closed window.");
            }
        }
    }

    private static WorkspaceUpdate BuildWorkspaceUpdate(
        WorkspaceState s,
        WorkspaceWindow[] windows,
        string? focusedViewId = null,
        WorkspaceViewRef[]? pins = null,
        WorkspaceViewRef[]? favourites = null) =>
        new([.. windows.SelectMany(w => w.Tabs)], focusedViewId ?? s.FocusedViewId, pins ?? s.Pins)
        {
            Windows = windows,
            Favourites = favourites ?? s.Favourites,
        };

    private static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) OpenDesktopAppInState(
        WorkspaceState s,
        string rawAppName,
        string? targetWindowId = null,
        bool asNewTab = false)
    {
        var appName = rawAppName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(appName) || appName == "desktop") { return (null, []); }
        var appKey = IntoChat.OS.Modules.Apps.IApp.Key("local", appName);
        var windows = s.Windows.Where(w => w.Tabs is { Length: > 0 }).ToList();

        if (!asNewTab)
        {
            foreach (var w in windows)
            {
                if (w.Tabs.FirstOrDefault(t => string.Equals(t.App, appKey, StringComparison.OrdinalIgnoreCase)) is { } existingTab)
                {
                    if (w.ActiveTabId == existingTab.Id && s.FocusedViewId == existingTab.Id) { return (null, []); }
                    var focusedWindows = windows.Select(win => win.Id == w.Id ? win with { ActiveTabId = existingTab.Id } : win).ToArray();
                    return (BuildWorkspaceUpdate(s, focusedWindows, existingTab.Id), []);
                }
            }
        }

        var takenTabIds = windows.SelectMany(w => w.Tabs).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var tabId = appName;
        for (var n = 2; !takenTabIds.Add(tabId); n++) { tabId = $"{appName}-{n}"; }
        var newTab = new WorkspaceViewRef(tabId, appKey, "full", string.Empty);

        if (asNewTab)
        {
            var targetWin = targetWindowId is { Length: > 0 }
                ? windows.FirstOrDefault(w => w.Id == targetWindowId)
                : windows.FirstOrDefault(w => w.Tabs.Any(t => t.Id == s.FocusedViewId)) ?? windows.LastOrDefault();
            if (targetWin is not null && targetWin.Tabs.Length < 8)
            {
                var updatedWins = windows.Select(w => w.Id == targetWin.Id ? new WorkspaceWindow(targetWin.Id, [.. targetWin.Tabs, newTab], newTab.Id) : w).ToArray();
                return (BuildWorkspaceUpdate(s, updatedWins, newTab.Id), []);
            }
        }

        if (windows.Count >= 8) { return (null, []); }

        var takenWinIds = windows.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var nextWinId = Enumerable.Range(1, 99).Select(n => $"w-{n}").First(id => !takenWinIds.Contains(id));
        return (BuildWorkspaceUpdate(s, [.. windows, new WorkspaceWindow(nextWinId, [newTab], newTab.Id)], newTab.Id), []);
    }

    private static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) CloseDesktopTargetInState(WorkspaceState s, string target)
    {
        var slash = target.LastIndexOf('/');
        var winId = slash >= 0 ? target[..slash] : target;
        var indexStr = slash >= 0 ? target[(slash + 1)..] : string.Empty;
        var windows = s.Windows.Where(w => w.Tabs is { Length: > 0 }).ToList();
        var window = windows.FirstOrDefault(w => w.Id == winId);
        if (window is null) { return (null, []); }

        if (slash < 0 || !int.TryParse(indexStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tabIndex))
        {
            tabIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
        }
        if (tabIndex < 0 || tabIndex >= window.Tabs.Length) { return (null, []); }

        if (window.Tabs.Length <= 1 || slash < 0)
        {
            var remaining = windows.Where(w => w.Id != winId).ToArray();
            var nextFocus = remaining.Any(w => w.Tabs.Any(t => t.Id == s.FocusedViewId)) ? s.FocusedViewId : remaining.LastOrDefault()?.ActiveTabId;
            return (BuildWorkspaceUpdate(s, remaining, nextFocus), [winId]);
        }

        var closedTabId = window.Tabs[tabIndex].Id;
        var nextTabs = window.Tabs.Where((_, i) => i != tabIndex).ToArray();
        var nextActive = window.ActiveTabId == closedTabId ? nextTabs[Math.Max(0, tabIndex - 1)].Id : window.ActiveTabId;
        var updatedWindows = windows.Select(w => w.Id == winId ? new WorkspaceWindow(winId, nextTabs, nextActive) : w).ToArray();
        return (BuildWorkspaceUpdate(s, updatedWindows, s.FocusedViewId == closedTabId ? nextActive : s.FocusedViewId), []);
    }

    private static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) CloseDesktopAppInState(WorkspaceState s, string rawAppName)
    {
        var appKey = IntoChat.OS.Modules.Apps.IApp.Key("local", rawAppName.Trim().ToLowerInvariant());
        var closedIds = new List<string>();
        var nextWindows = new List<WorkspaceWindow>();
        var changed = false;

        foreach (var w in s.Windows.Where(w => w.Tabs is { Length: > 0 }))
        {
            var keptTabs = w.Tabs.Where(t => !string.Equals(t.App, appKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (keptTabs.Length != w.Tabs.Length) { changed = true; }
            if (keptTabs.Length == 0) { closedIds.Add(w.Id); }
            else { nextWindows.Add(new WorkspaceWindow(w.Id, keptTabs, keptTabs.Any(t => t.Id == w.ActiveTabId) ? w.ActiveTabId : keptTabs[^1].Id)); }
        }

        if (!changed) { return (null, []); }
        var remaining = nextWindows.ToArray();
        var nextFocus = remaining.Any(w => w.Tabs.Any(t => t.Id == s.FocusedViewId)) ? s.FocusedViewId : remaining.LastOrDefault()?.ActiveTabId;
        return (BuildWorkspaceUpdate(s, remaining, nextFocus), closedIds);
    }

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (InvalidOperationException error)
        {
            NullLogger.Instance.LogSwallowed(error, $"{typeof(T).Name} is optional when DesktopApp runs in baseline tests.");
            return null;
        }
    }
}
