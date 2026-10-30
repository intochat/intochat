/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, sideload-list, sideload-install, sideload-add-git

The embedded folder scripts publish the Flutter settings tree, including the Modules and Apps tabs.
Sideload lists folders in the apps directory, installs one through the store path, and clones an https or ssh URL.
Preferences read and persist through the brain's UserSettingsNeuron (IUserSettings).
*/
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("settings.open")]
public sealed record SettingsOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.read")]
public sealed record SettingsRead : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.refresh-connections")]
public sealed record SettingsRefreshConnections : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.save-preferences")]
public sealed record SettingsSavePreferences(
    string? DisplayName = null,
    string? Theme = null,
    string? DefaultModel = null,
    string? StylePreset = null,
    string? BarLayout = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.reload-preferences")]
public sealed record SettingsReloadPreferences : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.reconcile-preferences")]
public sealed record SettingsReconcilePreferences : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.sideload-list")]
public sealed record SettingsSideloadList : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.sideload-install")]
public sealed record SettingsSideloadInstall(string? Name = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.sideload-add-git")]
public sealed record SettingsSideloadAddGit(string? Url = null) : DigitalBrain.Signal;

public sealed record SettingsAppState(
    string Title,
    string Status,
    string DisplayName,
    string Theme,
    string DefaultModel,
    Node? OverrideBody = null,
    string StylePreset = "fluent-acrylic",
    string BarLayout = "floating-glass")
{
    public SettingsAppState() : this("Settings", "Settings", "User", "dark", "qwen3.5:4b", null, "fluent-acrylic", "floating-glass") { }
}

[DigitalBrain.App("settings", Accent = "#64748B", Category = "System")]
public sealed class SettingsApp(IPresenter? presenter = null, SideloadDesk? sideload = null) : DigitalBrain.App<SettingsAppState>
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "settings", "Settings", "2.1.0", [typeof(UserSettingsNeuron)], new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = ShellPrograms.Load("Settings");

    [DigitalBrain.On]
    public async Task Open(SettingsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState("Settings", $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})", user.DisplayName, user.Theme, user.DefaultModel));
    }

    [DigitalBrain.On]
    public async Task Read(SettingsRead request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState("Settings", $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})", user.DisplayName, user.Theme, user.DefaultModel));
    }

    [DigitalBrain.On]
    public void RefreshConnections(SettingsRefreshConnections request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Update(State with { Title = "Settings", Status = "Connections", OverrideBody = null });
    }

    [DigitalBrain.On]
    public async Task SavePreferences(SettingsSavePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DisplayName)
            && string.IsNullOrWhiteSpace(request.Theme)
            && string.IsNullOrWhiteSpace(request.DefaultModel)
            && string.IsNullOrWhiteSpace(request.StylePreset)
            && string.IsNullOrWhiteSpace(request.BarLayout))
        {
            Update(State with { Title = "Settings", Status = "Nothing to save.", OverrideBody = null });
            return;
        }

        var settings = Get<IUserSettings>("settings");
        var updated = await settings.SetProfile(
            request.DisplayName ?? State.DisplayName,
            request.Theme ?? State.Theme,
            request.DefaultModel ?? State.DefaultModel);
        if (!string.IsNullOrWhiteSpace(request.StylePreset) || !string.IsNullOrWhiteSpace(request.BarLayout))
        {
            updated = await settings.SetUiIdentity(
                request.StylePreset ?? State.StylePreset,
                request.BarLayout ?? State.BarLayout);
        }
        Update(new SettingsAppState(
            "Settings",
            $"Saved: {updated.DisplayName} ({updated.Theme}, {updated.DefaultModel}, {updated.StylePreset}, {updated.BarLayout})",
            updated.DisplayName,
            updated.Theme,
            updated.DefaultModel,
            null,
            updated.StylePreset,
            updated.BarLayout));
    }

    [DigitalBrain.On]
    public async Task ReloadPreferences(SettingsReloadPreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState("Settings", $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})", user.DisplayName, user.Theme, user.DefaultModel));
    }

    [DigitalBrain.On]
    public void ReconcilePreferences(SettingsReconcilePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Update(State with { Title = "Settings", Status = "Settings", OverrideBody = null });
    }

    [DigitalBrain.On]
    public async Task SideloadList(SettingsSideloadList request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Collection([]))
            : await sideload.List(CancellationToken.None);
        Update(State with { Title = view.Title, OverrideBody = view.Root });
    }

    [DigitalBrain.On]
    public async Task SideloadInstall(SettingsSideloadInstall request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Text(string.IsNullOrWhiteSpace(request.Name) ? "Name an app." : request.Name))
            : await sideload.Install(request.Name, CancellationToken.None);
        Update(State with { Title = view.Title, OverrideBody = view.Root });
    }

    [DigitalBrain.On]
    public async Task SideloadAddGit(SettingsSideloadAddGit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Text(string.IsNullOrWhiteSpace(request.Url) ? "Name a git URL." : request.Url))
            : await sideload.AddGit(request.Url, CancellationToken.None);
        Update(State with { Title = view.Title, OverrideBody = view.Root });
    }

    public View RenderSide() => new(
        "Settings Sections",
        new Column(
        [
            new Surface(
                [
                    new TagPill($"{State.StylePreset} · {State.BarLayout}", "accent", "palette"),
                    new Text($"{State.DisplayName} · {State.DefaultModel}") { Tone = "muted", Tabular = true },
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "DigitalBrain Settings"),
            new Collection(
            [
                new Entry("appearance", "Appearance & UI Identity", $"{State.StylePreset} · {State.BarLayout} · {State.Theme}"),
                new Entry("models", "Models & AI", State.DefaultModel),
                new Entry("connections", "Connections", "Postgres, Google Drive, GitHub, Salesforce"),
                new Entry("circles", "Circles & Permissions", "Human-in-the-loop LFP approvals"),
                new Entry("compute", "Compute & Credits", "Local Ollama + Cloud Inference"),
                new Entry("apps", "Apps & Sideload", "C# App<TState> sandbox & Git sideload"),
                new Entry("about", "About", "IntoChat OS v0.9.0"),
            ]),
        ]) { Gap = 10 });

    public View Render() => new(
        State.Title,
        State.OverrideBody ?? new Column(
        [
            new Surface(
                [
                    new Heading("Appearance & UI Identity", Level: 2, Subtitle: "Switch live Acrylic glass presets, colour themes, and adaptive OS bar layouts."),
                    new Row(
                    [
                        new TagPill(State.StylePreset, "accent", "palette"),
                        new TagPill(State.BarLayout, "neutral", "layout"),
                        new TagPill(State.Theme, "ok", "sun"),
                    ]) { Gap = 8 },
                    new Row(
                    [
                        new Button("Fluent Acrylic", new ButtonAction("settings", "save-preferences", [State.DisplayName, State.Theme, State.DefaultModel, "fluent-acrylic", State.BarLayout]), Tone: State.StylePreset == "fluent-acrylic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                        new Button("Spatial Glass", new ButtonAction("settings", "save-preferences", [State.DisplayName, State.Theme, State.DefaultModel, "spatial-glass", State.BarLayout]), Tone: State.StylePreset == "spatial-glass" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                        new Button("Cyber Synaptic", new ButtonAction("settings", "save-preferences", [State.DisplayName, State.Theme, State.DefaultModel, "cyber-synaptic", State.BarLayout]), Tone: State.StylePreset == "cyber-synaptic" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                        new Button("Warm Paper", new ButtonAction("settings", "save-preferences", [State.DisplayName, State.Theme, State.DefaultModel, "warm-paper", State.BarLayout]), Tone: State.StylePreset == "warm-paper" ? ButtonTone.Accent : ButtonTone.Neutral) { Small = true },
                    ]) { Gap = 8 },
                    new Text(State.Status),
                ],
                Kind: SurfaceKind.Acrylic,
                Title: "1 · Appearance & UI Identity"),
            new Grid(
                2,
                [
                    new Surface(
                        [
                            new RecordCardNode(
                                "AI Runtime",
                                State.DefaultModel,
                                [
                                    new RecordFieldNode("Owner", State.DisplayName),
                                    new RecordFieldNode("Default Model", State.DefaultModel),
                                    new RecordFieldNode("Memory Neuron", "neuron://local/memory"),
                                ],
                                Subtitle: "2 · Models & AI"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "2 · Models & AI"),
                    new Surface(
                        [
                            new RecordCardNode(
                                "Capability Broker",
                                "Connections & Storage",
                                [
                                    new RecordFieldNode("Local Disk", "neuron://local/disk.files"),
                                    new RecordFieldNode("Postgres SQL", "neuron://local/postgres/main"),
                                    new RecordFieldNode("Confirmations", "neuron://local/os.confirmations/inbox"),
                                ],
                                Subtitle: "3 · Connections"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "3 · Connections"),
                    new Surface(
                        [
                            new RecordCardNode(
                                "Security",
                                "Circles & Permissions",
                                [
                                    new RecordFieldNode("Default Circle", "owner"),
                                    new RecordFieldNode("Write Gate", "LFP ApprovalCard"),
                                ],
                                Subtitle: "4 · Circles & Permissions"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "4 · Circles & Permissions"),
                    new Surface(
                        [
                            new RecordCardNode(
                                "Telemetry",
                                "Compute & Credits · Apps & Sideload · About",
                                [
                                    new RecordFieldNode("Compute", "Local Ollama + Cloud Credits"),
                                    new RecordFieldNode("Apps Sandbox", "IntoChat.OS.Modules.Microsoft.CSharp.Sandbox"),
                                    new RecordFieldNode("OS Version", "IntoChat OS v0.9.0"),
                                ],
                                Subtitle: "5–7 · Compute, Sideload & About"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "5–7 · Compute, Sideload & About"),
                ],
                Gap: 12),
        ]) { Gap = 14 });

    private void Update(SettingsAppState next)
    {
        Set(next);
        if (presenter is not null)
        {
            Present(presenter.Present, RenderSide, Render);
        }
    }

    [DigitalBrain.Scenario("Saving user preferences updates UserSettingsNeuron and view")]
    public async Task VerifySavePreferencesScenario()
    {
        await SavePreferences(new SettingsSavePreferences("Alice", "light", "gpt-5"));
        if (State.DisplayName != "Alice" || State.Theme != "light" || State.DefaultModel != "gpt-5")
        {
            throw new InvalidOperationException($"Expected Alice/light/gpt-5, got {State.DisplayName}/{State.Theme}/{State.DefaultModel}.");
        }
    }
}
