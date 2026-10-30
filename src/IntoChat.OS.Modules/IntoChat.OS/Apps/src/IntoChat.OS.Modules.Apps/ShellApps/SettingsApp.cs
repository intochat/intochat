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
public sealed record SettingsRefreshConnections(
    string? ConnectionKey = null,
    string? FriendApp = null) : DigitalBrain.Signal;

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

public sealed record SettingsSearchEntry(
    string SectionId,
    string Title,
    string Keywords,
    string ActionUri);

public sealed record SettingsAppState(
    string Title,
    string Status,
    string DisplayName,
    string Theme,
    string DefaultModel,
    Node? OverrideBody = null,
    string StylePreset = "fluent-acrylic",
    string BarLayout = "floating-glass",
    IReadOnlyList<string>? ConnectionKeys = null,
    IReadOnlyList<string>? FriendGrants = null)
{
    public static IReadOnlyList<string> DefaultConnectionKeys { get; } =
    [
        "neuron://local/connection/github/personal",
        "neuron://local/connection/github/work",
        "neuron://local/connection/google-drive/work",
        "neuron://local/local-cloud/main",
    ];

    public static IReadOnlyList<string> DefaultFriendGrants { get; } =
    [
        "sdk -> app:local/apps/creator (storage@1 · SANDBOX / TEST CREDENTIALS)",
        "sdk -> app:local/apps/ino (connections · ICapabilityBroker Zero-Token)",
    ];

    public SettingsAppState() : this(
        "Settings",
        "Settings",
        "User",
        "dark",
        "qwen3.5:4b",
        null,
        "fluent-acrylic",
        "floating-glass",
        DefaultConnectionKeys,
        DefaultFriendGrants)
    {
    }
}

[DigitalBrain.App("settings", Accent = "#64748B", Category = "System")]
public sealed class SettingsApp(IPresenter? presenter = null, SideloadDesk? sideload = null) : DigitalBrain.App<SettingsAppState>
{
    public static IReadOnlyList<SettingsSearchEntry> SearchIndex { get; } =
    [
        new("appearance", "Appearance & 7 UI Identities", "theme dark light glass acrylic mica carbon nordic", "os://window/settings?section=appearance"),
        new("profile", "Profile, Identity & 2FA", "avatar handle bio two-factor session", "os://window/profile"),
        new("models", "Models & AI Routing", "ollama claude gpt gemini local inference", "os://window/settings?section=models"),
        new("keys", "API Keys & Secure Vault", "secrets token encryption vault", "os://window/secure"),
        new("connections", "Connections & OAuth", "postgres salesforce google drive github tripletex integrations webhook", "os://window/settings?section=connections"),
        new("circles", "Circles & LFP Permissions", "approval human-in-the-loop grants", "os://window/settings?section=circles"),
        new("compute", "Compute & Credits", "tokens usage budget billing", "os://window/settings?section=compute"),
        new("apps", "Apps Store, Link Install & Sideload", "install reconsent sandbox git", "os://sheet/install"),
        new("modules", "Kernel Modules", "orleans grains runtime sdk cloud", "os://window/settings?section=modules"),
        new("variables", "Environment & Blackboard", "env vars scope", "os://window/settings?section=variables"),
        new("notifications", "Notifications & Focus", "inbox quiet hours", "os://window/settings?section=notifications"),
        new("archived", "Archived Sessions & File Conflicts", "snapshots two-version conflict", "os://sheet/conflict"),
        new("danger", "Danger Zone", "reset revoke export", "os://window/settings?section=danger"),
    ];

    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "settings", "Settings", "2.1.0", [typeof(UserSettingsNeuron)], new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = ShellPrograms.Load("Settings");

    [DigitalBrain.On]
    public async Task Open(SettingsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState(
            "Settings",
            $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})",
            user.DisplayName,
            user.Theme,
            user.DefaultModel,
            ConnectionKeys: State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys,
            FriendGrants: State.FriendGrants ?? SettingsAppState.DefaultFriendGrants));
    }

    [DigitalBrain.On]
    public async Task Read(SettingsRead request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState(
            "Settings",
            $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})",
            user.DisplayName,
            user.Theme,
            user.DefaultModel,
            ConnectionKeys: State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys,
            FriendGrants: State.FriendGrants ?? SettingsAppState.DefaultFriendGrants));
    }

    [DigitalBrain.On]
    public void RefreshConnections(SettingsRefreshConnections request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var keys = (State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys).ToList();
        if (!string.IsNullOrWhiteSpace(request.ConnectionKey) && !keys.Contains(request.ConnectionKey, StringComparer.Ordinal))
        {
            keys.Add(request.ConnectionKey);
        }
        var grants = (State.FriendGrants ?? SettingsAppState.DefaultFriendGrants).ToList();
        if (!string.IsNullOrWhiteSpace(request.FriendApp))
        {
            var grantEntry = $"sdk -> {request.FriendApp} ({request.ConnectionKey ?? "storage@1"})";
            if (!grants.Contains(grantEntry, StringComparer.Ordinal))
            {
                grants.Add(grantEntry);
            }
        }
        Update(State with
        {
            Title = "Settings",
            Status = "Connections",
            OverrideBody = null,
            ConnectionKeys = keys,
            FriendGrants = grants,
        });
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
            updated.BarLayout,
            State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys,
            State.FriendGrants ?? SettingsAppState.DefaultFriendGrants));
    }

    [DigitalBrain.On]
    public async Task ReloadPreferences(SettingsReloadPreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState(
            "Settings",
            $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})",
            user.DisplayName,
            user.Theme,
            user.DefaultModel,
            ConnectionKeys: State.ConnectionKeys ?? SettingsAppState.DefaultConnectionKeys,
            FriendGrants: State.FriendGrants ?? SettingsAppState.DefaultFriendGrants));
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
                Title: "DigitalBrain Settings (13 Sections)"),
            new Collection(
                SearchIndex.Select(s => new Entry(s.SectionId, s.Title, s.Keywords)).ToArray()),
        ])
        { Gap = 10 });

    public View Render() => new(
        State.Title,
        State.OverrideBody ?? new Column(
        [
            new Grid(
                3,
                [
                    Ui.StatCard("stat-preset", "Active UI Identity", State.StylePreset, State.BarLayout, BadgeTone.Accent, "palette"),
                    Ui.StatCard("stat-model", "Default Brain Model", State.DefaultModel, "Local + Cloud", BadgeTone.Done, "cpu"),
                    Ui.StatCard("stat-vault", "Zero-Leakage Vault", "4 Secrets Sealed", "2FA Active", BadgeTone.Neutral, "key-round"),
                ],
                Gap: 12),
            new Surface(
                [
                    new Heading("1 · Appearance & UI Identity", Level: 2, Subtitle: "Switch live Acrylic glass presets, colour themes, and adaptive OS bar layouts."),
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
            new Surface(
                [
                    new Heading("2 · Quick OS Windows & Security Sheets", Level: 3, Subtitle: "Declarative os:// window and sheet protocol across Profile, Secure Vault, App Store & File Conflict."),
                    new Row(
                    [
                        new Button("Open Profile & 2FA", new ButtonAction("os://window/profile", "os://window/profile", []), Tone: ButtonTone.Accent) { Small = true, Icon = "user" },
                        new Button("Open Secure Vault", new ButtonAction("os://window/secure", "os://window/secure", [])) { Small = true, Icon = "key-round" },
                        new Button("Install App by Link", new ButtonAction("os://sheet/install", "os://sheet/install", [])) { Small = true, Icon = "package-plus" },
                        new Button("Review Update Consent", new ButtonAction("os://sheet/reconsent", "os://sheet/reconsent", [])) { Small = true, Icon = "shield-check" },
                        new Button("Resolve File Conflict", new ButtonAction("os://sheet/conflict", "os://sheet/conflict", [])) { Small = true, Icon = "git-compare" },
                    ]) { Gap = 8 },
                ],
                Kind: SurfaceKind.Elevated,
                Title: "2 · Profile, Secure Vault & Modal Sheets"),
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
                                Subtitle: "3 · Models & AI"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "3 · Models & AI"),
                    new Surface(
                        [
                            new RecordCardNode(
                                "Capability Broker",
                                "Connections & Storage",
                                [
                                    new RecordFieldNode("Local Disk", "neuron://local/disk.files"),
                                    new RecordFieldNode("Local Cloud", "neuron://local/local-cloud/main"),
                                    new RecordFieldNode("GitHub (Personal)", "neuron://local/connection/github/personal"),
                                    new RecordFieldNode("GitHub (Work)", "neuron://local/connection/github/work"),
                                    new RecordFieldNode("Google Drive (Work)", "neuron://local/connection/google-drive/work"),
                                    new RecordFieldNode("Postgres SQL", "neuron://local/postgres/main"),
                                    new RecordFieldNode("Confirmations", "neuron://local/os.confirmations/inbox"),
                                    new RecordFieldNode("Unified OAuth & Webhooks", "/integrations/{provider}/oauth/callback · /integrations/{provider}/webhook"),
                                    new RecordFieldNode("Zero-Token Broker", "ICapabilityBroker · ConnectionNeuron.UseCredentialAsync (Active)"),
                                    new RecordFieldNode("Sandbox & Friend Sharing", "sdk · ShareWith(app:*) · SANDBOX / TEST CREDENTIALS"),
                                ],
                                Subtitle: "4 · Connections & API Keys"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "4 · Connections & API Keys"),
                    new Surface(
                        [
                            new RecordCardNode(
                                "Security",
                                "Circles & Permissions",
                                [
                                    new RecordFieldNode("Default Circle", "owner"),
                                    new RecordFieldNode("Write Gate", "LFP ApprovalCard"),
                                    new RecordFieldNode("2FA Policy", "Hardware TOTP Active"),
                                ],
                                Subtitle: "5–8 · Circles, Modules, Variables & Notifications"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "5–8 · Circles, Modules, Variables & Notifications"),
                    new Surface(
                        [
                            new RecordCardNode(
                                "Telemetry & Store",
                                "Compute & Credits · Apps & Sideload · Danger Zone",
                                [
                                    new RecordFieldNode("Compute", "Local Ollama + Cloud Credits"),
                                    new RecordFieldNode("Apps Sandbox", "IntoChat.OS.Modules.Microsoft.CSharp.Sandbox"),
                                    new RecordFieldNode("OS Version", "IntoChat OS v0.9.1"),
                                ],
                                Subtitle: "9–13 · Compute, Apps, Archived & Danger Zone"),
                        ],
                        Kind: SurfaceKind.Elevated,
                        Title: "9–13 · Compute, Apps, Archived & Danger Zone"),
                ],
                Gap: 12),
        ])
        { Gap = 14 });

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
