/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow

The embedded folder scripts publish the Flutter desktop. These operations present Smart Desktop state and 3D BrainScene telemetry.
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

public sealed record DesktopAppState(
    string LayoutMode,
    bool IsNarrow,
    string StylePreset,
    string BarLayout,
    int ActiveNeurons,
    string Status)
{
    public DesktopAppState() : this("smart-desktop", false, "fluent-acrylic", "floating-glass", 7, "Smart Desktop Ready") { }
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

    public View RenderSide() => new(
        "Launcher",
        new Collection(
        [
            new Entry("ino", "Ino Assistant", "neuron://local/apps/ino"),
            new Entry("files", "Files", "neuron://local/apps/files"),
            new Entry("gallery", "UI Gallery", "neuron://local/apps/gallery"),
            new Entry("creator", "Creator 3D Loop", "neuron://local/apps/creator"),
            new Entry("post", "Post", "neuron://local/apps/post"),
            new Entry("settings", "Settings", "neuron://local/apps/settings"),
        ]));

    public View Render() => new(
        "Desktop",
        new Column(
        [
            new Surface(
                [
                    new Row(
                    [
                        new TagPill(State.StylePreset, "accent", "palette"),
                        new TagPill(State.BarLayout, "neutral", "layout"),
                        new TagPill($"{State.ActiveNeurons} active neurons", "ok", "cpu"),
                    ]) { Gap = 8 },
                    new Text("Desktop"),
                    new Text($"{State.Status} · {State.StylePreset} · {State.BarLayout} · 3D BrainScene ({State.ActiveNeurons} neurons)") { Tabular = true },
                    new Text("Smart Brief: 2 approvals waiting in neuron://local/os.confirmations/inbox"),
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "Ino · DigitalBrain OS"),
            new BrainSceneNode(
                [
                    new BrainGraphNode("os", "OperatingSystem", "neuron://local/os", "core", 0, 0, 0, "working"),
                    new BrainGraphNode("ino", "Ino Assistant", "neuron://local/apps/ino", "agent", 110, 35, -20, "working"),
                    new BrainGraphNode("files", "Files", "neuron://local/apps/files", "storage", -115, 25, 15, "idle"),
                    new BrainGraphNode("gallery", "UI Gallery", "neuron://local/apps/gallery", "ui", 85, -60, 25, "idle"),
                    new BrainGraphNode("creator", "Creator 3D Loop", "neuron://local/apps/creator", "studio", -90, -55, -25, "working"),
                ],
                [
                    new BrainGraphSynapse("os", "ino", "InoSend", true),
                    new BrainGraphSynapse("ino", "files", "FilesSearch", false),
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
        ]) { Gap = 14 });

    [DigitalBrain.Scenario("Smart Desktop renders 3D BrainScene, UI identity preset, and launcher apps")]
    public async Task VerifySmartDesktopScenario()
    {
        await Start(new DesktopStart());
        if (State.ActiveNeurons < 5 || string.IsNullOrWhiteSpace(State.StylePreset))
        {
            throw new InvalidOperationException("Expected Smart Desktop to initialize 3D BrainScene neuron count and UI identity.");
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
            ActiveNeurons = 7,
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
