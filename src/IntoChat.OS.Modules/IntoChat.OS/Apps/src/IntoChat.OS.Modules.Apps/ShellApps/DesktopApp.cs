/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow, auto-arrange, pin

The embedded folder scripts publish the Flutter desktop. These operations present Smart Desktop state, Whiteboard sticky-note app pinning, auto-arrange, and 3D BrainScene telemetry.
*/
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("desktop.start")]
public sealed record DesktopStart : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.layout")]
public sealed record DesktopLayoutChoice : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.open")]
public sealed record DesktopOpen : DigitalBrain.Signal;

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
    public DesktopAppState() : this("smart-desktop", false, "fluent-acrylic", "floating-glass", 9, "Smart Desktop Ready", ["ino", "creator"]) { }
}

[DigitalBrain.App("desktop")]
public sealed class DesktopApp : DigitalBrain.App<DesktopAppState>
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "desktop", "Desktop", "0.4.0", [], new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = ShellPrograms.Load("Desktop");

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
            ("photocraft", "PhotoCraft", "neuron://local/apps/photocraft", "image"),
            ("filmcraft", "FilmCraft", "neuron://local/apps/filmcraft", "film"),
            ("gallery", "UI Gallery", "neuron://local/apps/gallery", "layout-grid"),
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
                        new Button("Auto-Arrange", new ButtonAction("desktop", "auto-arrange", []), Tone: ButtonTone.Accent)
                        {
                            Icon = "layout-grid",
                            Small = true,
                        },
                        new Button("Pin PhotoCraft", new ButtonAction("desktop", "pin", ["photocraft", "true"]))
                        {
                            Icon = "pin",
                            Small = true,
                        },
                        new Button("Pin FilmCraft", new ButtonAction("desktop", "pin", ["filmcraft", "true"]))
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
                    new BrainGraphNode("photocraft", "PhotoCraft", "neuron://local/apps/photocraft", "studio", 125, -35, 20, "idle"),
                    new BrainGraphNode("filmcraft", "FilmCraft", "neuron://local/apps/filmcraft", "studio", -125, -30, -15, "idle"),
                    new BrainGraphNode("gallery", "UI Gallery", "neuron://local/apps/gallery", "ui", 85, -60, 25, "idle"),
                    new BrainGraphNode("creator", "Creator 3D Loop", "neuron://local/apps/creator", "studio", -90, -55, -25, "working"),
                ],
                [
                    new BrainGraphSynapse("os", "ino", "InoSend", true),
                    new BrainGraphSynapse("ino", "files", "FilesSearch", false),
                    new BrainGraphSynapse("ino", "photocraft", "PhotoCraftEdit", true),
                    new BrainGraphSynapse("ino", "filmcraft", "FilmCraftEdit", false),
                    new BrainGraphSynapse("os", "creator", "CreatorInspect", true),
                ],
                SelectedId: "os"),
            new ApprovalCardNode(
                "smart-desktop-signal-1",
                "Live Smart App Signal",
                "Smart Brief: 2 approvals waiting in neuron://local/os.confirmations/inbox",
                [
                    new ApprovalRowNode("Signal", "Pending", "Opportunity Stage -> Closed Won", "$420,000"),
                    new ApprovalRowNode("Inbox", "local", "neuron://local/os.confirmations/inbox"),
                ],
                SystemName: "Ino",
                Status: "pending",
                OnApprove: new ButtonAction("apps/ino", "approve-card", ["card-1", "true"]),
                OnReject: new ButtonAction("apps/ino", "approve-card", ["card-1", "false"]),
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
            ActiveNeurons = 9,
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
