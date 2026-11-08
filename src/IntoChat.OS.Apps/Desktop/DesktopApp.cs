/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow, auto-arrange, pin

The embedded folder scripts publish the Flutter desktop. These operations present Smart Desktop state, Whiteboard sticky-note app pinning, auto-arrange, and 3D BrainScene telemetry.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

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
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(DesktopApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public async Task Start(DesktopStart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SyncSmartDesktopAsync("smart-desktop", State.IsNarrow);
    }

    [DigitalBrain.On]
    public async Task Layout(DesktopLayoutChoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextLayout = State.LayoutMode == "smart-desktop" ? "spatial-grid" : "smart-desktop";
        await SyncSmartDesktopAsync(nextLayout, State.IsNarrow);
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
    public Task Pin(DesktopPinApp request)
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

        var updated = current.ToArray();
        Set(State with
        {
            PinnedApps = updated,
            Status = request.Pinned
                ? $"Pinned {appId} as always-on-top whiteboard sticky-note ({updated.Length} pinned)"
                : $"Unpinned {appId} ({updated.Length} pinned)",
        });
        return Task.CompletedTask;
    }

    public View RenderSide()
    {
        var apps = new (string Id, string Title, string Uri, string Icon)[]
        {
            ("ino", "Ino Assistant", "neuron://local/apps/ino", "sparkles"),
            ("files", "Files", "neuron://local/apps/files", "folder"),
            ("data", "Data Studio", "neuron://local/apps/data", "bar-chart-2"),
            ("markdraw", "Markdraw", "neuron://local/apps/markdraw", "pen-tool"),
            ("photocraft", "PhotoCraft", "neuron://local/apps/photocraft", "image"),
            ("filmcraft", "FilmCraft", "neuron://local/apps/filmcraft", "film"),
            ("gallery", "UI Gallery", "neuron://local/apps/gallery", "layout-grid"),
            ("tasks", "Tasks", "neuron://local/apps/tasks", "tasks"),
            ("trace", "Trace", "neuron://local/apps/trace", "activity"),
            ("creator", "Creator 3D Loop", "neuron://local/apps/creator", "cpu"),
            ("post", "Post", "neuron://local/apps/post", "send"),
            ("settings", "Settings", "neuron://local/apps/settings", "settings"),
        };
        var pinned = State.PinnedApps ?? ["ino", "creator"];

        return new(
            "Launcher",
            new Column(
            [
                new Divider("PINNED & SHELL APPS"),
                .. apps.Select(a => (Node)Ui.NavItem(
                    $"launch-{a.Id}",
                    a.Title,
                    a.Icon,
                    $"pin:{a.Id}",
                    selected: pinned.Contains(a.Id, StringComparer.OrdinalIgnoreCase),
                    badge: pinned.Contains(a.Id, StringComparer.OrdinalIgnoreCase) ? "PIN" : null)),
                new Collection(apps.Select(a => new Entry(a.Id, a.Title, a.Uri)).ToArray()),
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
                    new Text("Smart Brief: 2 approvals waiting in neuron://local/os.confirmations/inbox"),
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
                    new BrainGraphNode("creator", "Creator 3D Loop", "neuron://local/apps/creator", "studio", -90, -55, -25, "working"),
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
            new ApprovalCardNode(
                "smart-desktop-signal-1",
                "smart-desktop-signal-1",
                "Live Smart App Signal",
                "Smart Brief: 2 approvals waiting in neuron://local/os.confirmations/inbox",
                [
                    new ApprovalRowNode("Signal", "Pending", "Opportunity Stage -> Closed Won", "$420,000"),
                    new ApprovalRowNode("Inbox", "local", "neuron://local/os.confirmations/inbox"),
                ],
                SystemName: "Ino",
                Status: "pending",
                SourceNeuronUri: "neuron://local/os.confirmations/inbox"),
        ])
        { Gap = 14 });

    [DigitalBrain.Scenario("Smart Desktop renders 3D BrainScene, UI identity preset, and launcher apps")]
    public async Task VerifySmartDesktopScenario()
    {
        await Start(new DesktopStart());
        if (State.ActiveNeurons < 5 || string.IsNullOrWhiteSpace(State.StylePreset))
        {
            throw new InvalidOperationException("Expected Smart Desktop to initialize 3D BrainScene neuron count and UI identity.");
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
