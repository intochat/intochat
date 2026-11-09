/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow, auto-arrange, pin

The embedded folder scripts publish the Flutter desktop. These operations present Smart Desktop state, Whiteboard sticky-note app pinning, auto-arrange, and 3D BrainScene telemetry.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;

namespace IntoChat.OS.Apps.Desktop;

[DigitalBrain.Signal("desktop.start")]
public sealed record DesktopStart : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.layout")]
public sealed record DesktopLayoutChoice : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.open")]
public sealed record DesktopOpen(string? App = null, string? View = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.narrow")]
public sealed record DesktopNarrow(string? Narrow = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.auto-arrange")]
public sealed record DesktopAutoArrange : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.widget-open")]
public sealed record DesktopWidgetOpen(string? Widget = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.widget-close")]
public sealed record DesktopWidgetClose(string? Widget = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.pin")]
public sealed record DesktopPinApp(string? AppId = null, bool Pinned = true) : DigitalBrain.Signal;

public sealed record DesktopAppState(
    string LayoutMode,
    bool IsNarrow,
    string StylePreset,
    string BarLayout,
    int ActiveNeurons,
    string Status,
    string[] PinnedApps)
{
    public DesktopAppState() : this("smart-desktop", false, "fluent-acrylic", "floating-glass", 10, "Smart Desktop Ready", ["ino", "creator"]) { }
}

[DigitalBrain.App("desktop")]
public sealed class DesktopApp : DigitalBrain.App<DesktopAppState>
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(DesktopApp));

    public const string AppName = "desktop";

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

    // The desktop's durable records (widgets, greeted, layout); State is this activation's working state.
    private IDesktopRecords Records => Get<IDesktopRecords>(IDesktopRecords.Key);
    private IntoChat.OS.Modules.Apps.Approvals.Approval[] _approvals = [];

    public static readonly IReadOnlyList<(string Id, string Title, string Uri, string Icon, string Category, string RuntimeMode)> NativeCatalog =
    [
        // DigitalBrain OS Native Grains (10)
        ("ino", "Ino Assistant", "neuron://local/apps/ino", "sparkles", "Intelligence", "orleans-grain"),
        ("files", "Files", "neuron://local/apps/files", "folder", "Workspace", "orleans-grain"),
        ("workspace", "Workspace", "neuron://local/apps/workspace", "compass", "Workspace", "orleans-grain"),
        ("tasks", "Tasks", "neuron://local/apps/tasks", "tasks", "Workspace", "orleans-grain"),
        ("data", "Data Studio", "neuron://local/apps/data", "bar-chart-2", "Studio", "orleans-grain"),
        ("markdraw", "Markdraw", "neuron://local/apps/markdraw", "pen-tool", "Studio", "orleans-grain"),
        ("creator", "Creator 3D Loop", "neuron://local/apps/creator", "cpu", "Studio", "orleans-grain"),
        ("gallery", "UI Gallery", "neuron://local/apps/gallery", "layout-grid", "Studio", "orleans-grain"),
        ("friends", "Friends", "neuron://local/apps/friends", "users", "Social", "orleans-grain"),
        ("settings", "Settings", "neuron://local/apps/settings", "settings", "System", "orleans-grain"),
    ];

    public static IReadOnlyList<(string Id, string Title, string Uri, string Icon, string Category, string RuntimeMode)> Catalog { get; } =
        NativeCatalog.Concat(
            ArtCraftCatalog.All.Select(app => (app.Id, app.Title, $"neuron://local/apps/{app.Id}", app.Icon, app.Category, "wasm-ui+mcp+cli"))
        ).ToArray();

    [DigitalBrain.On]
    public async Task Start(DesktopStart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var records = await Records.Read();
        await SyncSmartDesktopAsync(records.Layout is { Length: > 0 } layout ? layout : "smart-desktop", State.IsNarrow);
        if (!records.Greeted)
        {
            // Ino greets once: the flag is the desktop's record, so another activation or a restart does not greet again.
            try { await Get<IntoChat.OS.Modules.Apps.IApp>("apps/ino").Request("open", "greet"); }
            catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "Ino was not there to greet; the desktop starts without it."); }
            await Records.Write(records with { Greeted = true });
        }
    }

    [DigitalBrain.On]
    public async Task WidgetOpen(DesktopWidgetOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var records = await Records.Read();
        if (WidgetId(request.Widget) is { } id && !records.Widgets.Contains(id, StringComparer.Ordinal)) { await Records.Write(records with { Widgets = [.. records.Widgets, id] }); }
    }

    [DigitalBrain.On]
    public async Task WidgetClose(DesktopWidgetClose request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var records = await Records.Read();
        if (WidgetId(request.Widget) is { } id && records.Widgets.Contains(id, StringComparer.Ordinal))
        { await Records.Write(records with { Widgets = [.. records.Widgets.Where(known => known != id)] }); }
    }

    private static string? WidgetId(string? widget) => string.IsNullOrWhiteSpace(widget) ? null : widget.Trim().ToLowerInvariant();

    [DigitalBrain.On]
    public async Task Layout(DesktopLayoutChoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextLayout = State.LayoutMode == "smart-desktop" ? "spatial-grid" : "smart-desktop";
        await SyncSmartDesktopAsync(nextLayout, State.IsNarrow);
        await Records.Write(await Records.Read() with { Layout = nextLayout });
    }

    [DigitalBrain.On]
    public async Task Open(DesktopOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Only a typed open with a view forwards it to the app (its open operation receives the view as input); a legacy open by
        // name ("open:{app}") binds App alone and is the window open it always was. A failed forward still opens the desktop.
        if (!string.IsNullOrWhiteSpace(request.App) && request.View is { } view)
        {
            try { await Get<IntoChat.OS.Modules.Apps.IApp>($"apps/{request.App.Trim().ToLowerInvariant()}").Request("open", view.Trim()); }
            catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The app did not take the view; the desktop opens without it."); }
        }
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [DigitalBrain.On]
    public async Task Narrow(DesktopNarrow request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var narrow = string.Equals(request.Narrow, "true", StringComparison.OrdinalIgnoreCase);
        await SyncSmartDesktopAsync(State.LayoutMode, narrow);
    }

    [DigitalBrain.On]
    public Task AutoArrange(DesktopAutoArrange request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pinnedList = State.PinnedApps is { Length: > 0 } ? State.PinnedApps : ["ino", "creator"];
        Set(State with
        {
            LayoutMode = "whiteboard-auto-arranged",
            PinnedApps = pinnedList,
            Status = $"Whiteboard Auto-Arranged · {pinnedList.Length} pinned sticky-note app(s) locked on top ({string.Join(", ", pinnedList)})",
        });
        return Task.CompletedTask;
    }

    [DigitalBrain.On]
    public async Task Pin(DesktopPinApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appId = string.IsNullOrWhiteSpace(request.AppId) ? "photocraft" : request.AppId.Trim().ToLowerInvariant();
        var current = (State.PinnedApps ?? ["ino", "creator"]).ToList();
        if (request.Pinned)
        {
            if (!current.Contains(appId, StringComparer.OrdinalIgnoreCase))
            {
                current.Add(appId);
            }
        }
        else
        {
            current.RemoveAll(item => string.Equals(item, appId, StringComparison.OrdinalIgnoreCase));
        }

        try
        {
            var workspace = Get<IntoChat.OS.Modules.Flutter.Workspace.IWorkspace>("workspace");
            var wsState = await workspace.Read();
            var appKey = IntoChat.OS.Modules.Apps.IApp.Key("local", appId);
            var alreadyPinned = wsState.Pins.Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase));
            if (alreadyPinned != request.Pinned)
            {
                IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef[] nextPins = request.Pinned
                    ? [.. wsState.Pins, new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef(appId, appKey, "full", string.Empty)]
                    : [.. wsState.Pins.Where(p => !string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase))];
                await workspace.Apply(
                    new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceUpdate(wsState.OpenViews, wsState.FocusedViewId, nextPins)
                    {
                        Windows = wsState.Windows,
                        Favourites = wsState.Favourites,
                    },
                    $"desktop-pin:{appId}:{request.Pinned}",
                    wsState.Revision);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "WorkspaceNeuron is optional when DesktopApp runs in standalone tests.");
        }

        var updated = current.ToArray();
        Set(State with
        {
            PinnedApps = updated,
            Status = request.Pinned
                ? $"Pinned {appId} as always-on-top whiteboard sticky-note ({updated.Length} pinned)"
                : $"Unpinned {appId} ({updated.Length} pinned)",
        });
    }

    public View RenderSide()
    {
        var pinned = State.PinnedApps ?? ["ino", "creator"];

        return new(
            "Launcher",
            new Column(
            [
                new Divider("PINNED & SHELL APPS"),
                .. Catalog.Select(a => (Node)Ui.NavItem(
                    $"launch-{a.Id}",
                    a.Title,
                    a.Icon,
                    $"pin:{a.Id}",
                    selected: pinned.Contains(a.Id, StringComparer.OrdinalIgnoreCase),
                    badge: pinned.Contains(a.Id, StringComparer.OrdinalIgnoreCase) ? "PIN" : a.Category)),
            ])
            { Gap = 6 });
    }

    public View Render() => new(
        "Desktop",
        new Column(
        [
            new Grid(
                3,
                [
                    Ui.StatCard("desk-neurons", "Active Neurons", $"{State.ActiveNeurons}", "3D Graph", BadgeTone.Done, "cpu"),
                    Ui.StatCard("desk-pinned", "Pinned Sticky-Notes", $"{State.PinnedApps.Length}", "Locked", BadgeTone.Accent, "pin"),
                    Ui.StatCard("desk-preset", "Theme Preset", State.StylePreset, State.BarLayout, BadgeTone.Accent, "palette"),
                ],
                Gap: 10),
            new Surface(
                [
                    new Row(
                    [
                        new TagPill(State.StylePreset, "accent", "palette"),
                        new TagPill(State.BarLayout, "neutral", "layout"),
                        new TagPill($"{State.ActiveNeurons} active neurons", "ok", "cpu"),
                        new TagPill($"{State.PinnedApps.Length} Pinned Sticky-Notes", "accent", "pin"),
                    ]) { Gap = 8 },
                    new Text("Desktop"),
                    new Text($"{State.Status} · {State.StylePreset} · {State.BarLayout} · 3D BrainScene ({State.ActiveNeurons} neurons)") { Tabular = true },
                    new Text($"Whiteboard Sticky-Note Pins (Locked & Always-on-Top): {string.Join(", ", State.PinnedApps)}") { Tabular = true },
                    new Row(
                    [
                        new Button("auto-arrange", "Auto-Arrange", Emit: AppSignals.Emit("desktop", "auto-arrange"), Tone: ButtonTone.Accent)
                        {
                            Icon = "layout-grid",
                            Small = true,
                        },
                        new Button("pin-markdraw", "Pin Markdraw", Emit: AppSignals.Emit("desktop", "pin", "markdraw", "true"))
                        {
                            Icon = "pin",
                            Small = true,
                        },
                        new Button("pin-photocraft", "Pin PhotoCraft", Emit: AppSignals.Emit("desktop", "pin", "photocraft", "true"))
                        {
                            Icon = "pin",
                            Small = true,
                        },
                        new Button("pin-filmcraft", "Pin FilmCraft", Emit: AppSignals.Emit("desktop", "pin", "filmcraft", "true"))
                        {
                            Icon = "pin",
                            Small = true,
                        },
                    ]) { Gap = 8 },
                    new Text(_approvals.Length == 0 ? "No pending approvals" : $"Smart Brief: {_approvals.Length} approval(s) waiting"),
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "Ino · DigitalBrain OS"),
            new BrainSceneNode(
                [
                    new BrainGraphNode("os", "OperatingSystem", "neuron://local/os", "core", 0, 0, 0, "working"),
                    new BrainGraphNode("ino", "Ino Assistant", "neuron://local/apps/ino", "agent", 110, 35, -20, "working"),
                    new BrainGraphNode("files", "Files", "neuron://local/apps/files", "storage", -115, 25, 15, "idle"),
                    new BrainGraphNode("data", "Data Studio", "neuron://local/apps/data", "analytics", 15, -75, 20, "working"),
                    new BrainGraphNode("markdraw", "Markdraw", "neuron://local/apps/markdraw", "studio", 45, 70, 30, "working"),
                    new BrainGraphNode("photocraft", "PhotoCraft", "neuron://local/apps/photocraft", "studio", 125, -35, 20, "idle"),
                    new BrainGraphNode("filmcraft", "FilmCraft", "neuron://local/apps/filmcraft", "studio", -125, -30, -15, "idle"),
                    new BrainGraphNode("gallery", "UI Gallery", "neuron://local/apps/gallery", "ui", 85, -60, 25, "idle"),
                    new BrainGraphNode("creator", "Creator", "neuron://local/apps/creator", "studio", -90, -55, -25, "working"),
                ],
                [
                    new BrainGraphSynapse("os", "ino", "InoSend", true),
                    new BrainGraphSynapse("ino", "files", "FilesSearch", false),
                    new BrainGraphSynapse("ino", "markdraw", "MarkdrawPatchElements", true),
                    new BrainGraphSynapse("ino", "photocraft", "PhotoCraftEdit", true),
                    new BrainGraphSynapse("ino", "filmcraft", "FilmCraftEdit", false),
                    new BrainGraphSynapse("os", "creator", "CreatorInspect", true),
                ],
                SelectedId: "os"),
            .. _approvals.Select(approval => (Node)new ApprovalCardNode(
                $"approval-{approval.PlanId}",
                approval.PlanId,
                approval.Title,
                approval.Detail,
                [new ApprovalRowNode("Subject", "", approval.Subject)],
                SystemName: "Ino",
                Status: "pending",
                Resolve: new Resolve(approval.PlanId))),
        ])
        { Gap = 14 });

    [DigitalBrain.Scenario("Smart Desktop renders 3D BrainScene, UI identity preset, and launcher apps")]
    public async Task VerifySmartDesktopScenario()
    {
        await Start(new DesktopStart());
        if (State.ActiveNeurons < 5 || string.IsNullOrWhiteSpace(State.StylePreset) || Catalog.Count < 25)
        {
            throw new InvalidOperationException("Expected Smart Desktop to initialize 3D BrainScene neuron count, UI identity, and app catalog.");
        }

        await Pin(new DesktopPinApp("photocraft", Pinned: true));
        if (!State.PinnedApps.Contains("photocraft", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Expected DesktopApp.Pin to pin photocraft as an always-on-top sticky-note app.");
        }

        await AutoArrange(new DesktopAutoArrange());
        if (!State.PinnedApps.Contains("photocraft", StringComparer.OrdinalIgnoreCase)
            || !State.Status.Contains("Auto-Arranged", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Expected DesktopApp.AutoArrange to preserve pinned apps and arrange unpinned windows.");
        }
    }

    private async Task SyncSmartDesktopAsync(string layoutMode, bool narrow)
    {
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetSettings() is { } settings)
        {
            var current = await settings.Read();
            preset = current.StylePreset;
            barLayout = current.BarLayout;
        }

        try
        {
            _approvals = [.. await Get<IntoChat.OS.Modules.Apps.Approvals.IApprovals>("approvals/index").Pending()];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "Approvals inbox is optional when DesktopApp runs in baseline tests.");
        }

        Set(State with
        {
            LayoutMode = layoutMode,
            IsNarrow = narrow,
            StylePreset = preset,
            BarLayout = barLayout,
            ActiveNeurons = 10,
            Status = narrow ? "Smart Desktop (Compact)" : $"Smart Desktop ({layoutMode})",
        });
    }

    private IUserSettings? TryGetSettings()
    {
        try
        {
            return Get<IUserSettings>("settings");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "UserSettingsNeuron is optional when DesktopApp runs in baseline tests.");
            return null;
        }
    }
}
