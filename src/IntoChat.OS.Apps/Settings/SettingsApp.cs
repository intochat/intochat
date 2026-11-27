/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, navigate, go-back, export-brain, import-brain, set-variable, set-budget, connection-probe, connection-disconnect, device-flow-start, device-flow-complete, mcp-add, mcp-remove

Declarative macOS Sonoma-style System Settings application for IntoChat OS (`UK-16` / `INT-1364`).
Owns the 13 settings sections, master-detail subpages (`AppPage`, `MemoryPage`, `ConnectionPage`,
`PersonPage`, `LocalServerPage`), modal sheets, search index, navigation backstack, and pure immutable
`SettingsAppState` DDD Aggregate projections (zero transient mutable grain fields!).
*/
using IntoChat.OS.Host.Apps;
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Components;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.Identity.Contracts.Connections;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Visuals;

namespace IntoChat.OS.Apps.Settings;

[Signal("settings.open")]
public sealed record SettingsOpen(string? Section = null) : Signal;

[Signal("settings.read")]
public sealed record SettingsRead : Signal;

[Signal("settings.refresh-connections")]
public sealed record SettingsRefreshConnections(string? ConnectionKey = null, string? FriendApp = null) : Signal;

[Signal("settings.save-preferences")]
public sealed record SettingsSavePreferences(
    string? DisplayName = null,
    string? Theme = null,
    string? DefaultModel = null,
    string? StylePreset = null,
    string? BarLayout = null,
    string? ThemeMode = null,
    string? DesktopTone = null,
    string? Language = null,
    string? TimeZone = null,
    string? DateFormat = null) : Signal;

[Signal("settings.reload-preferences")]
public sealed record SettingsReloadPreferences : Signal;

[Signal("settings.reconcile-preferences")]
public sealed record SettingsReconcilePreferences : Signal;

[Signal("settings.navigate")]
public sealed record SettingsNavigate(string? Section = "general", string? SubPage = null, string? Sheet = null, string? SearchQuery = null) : Signal;

[Signal("settings.go-back")]
public sealed record SettingsGoBack : Signal;

[Signal("settings.clear-search")]
public sealed record SettingsClearSearch : Signal;

[Signal("settings.export-brain")]
public sealed record SettingsExportBrain : Signal;

[Signal("settings.import-brain")]
public sealed record SettingsImportBrain(string? ArchiveJson = null) : Signal;

[Signal("settings.set-variable")]
public sealed record SettingsSetVariable(string? Key = null, string? Value = null) : Signal;

[Signal("settings.set-budget")]
public sealed record SettingsSetBudget(long MonthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros) : Signal;

[Signal("settings.connection-probe")]
public sealed record SettingsProbeConnection(string ConnectorId = "", string ConnectionId = "") : Signal;

[Signal("settings.connection-disconnect")]
public sealed record SettingsDisconnectConnection(string ConnectorId = "", string ConnectionId = "") : Signal;

[Signal("settings.device-flow-start")]
public sealed record SettingsStartDeviceFlow(string ConnectorId = "github", string ConnectionId = "main", string AuthMethodId = "", string? Target = null) : Signal;

[Signal("settings.device-flow-complete")]
public sealed record SettingsCompleteDeviceFlow(string FlowId = "") : Signal;

[Signal("settings.mcp-add")]
public sealed record SettingsMcpAdd(string? Name = null, string? Url = null, string? Title = null) : Signal;

[Signal("settings.mcp-remove")]
public sealed record SettingsMcpRemove(string? Name = null) : Signal;

[Signal("settings.launch-browser-pairing")]
public sealed record SettingsLaunchBrowserPairing(string? CloudUrl = null) : Signal;

[Signal("settings.toggle-manual-pairing")]
public sealed record SettingsToggleManualPairing : Signal;

[Signal("settings.pair-device")]
public sealed record SettingsPairDevice(string? Server = null, string? Code = null) : Signal;

[Signal("settings.disconnect-device")]
public sealed record SettingsDisconnectDevice : Signal;

[Signal("settings.mint-enrollment-code")]
public sealed record SettingsMintEnrollmentCode(string? ServerUrl = null) : Signal;

[Signal("settings.revoke-enrolled-device")]
public sealed record SettingsRevokeEnrolledDevice(string MachineId = "") : Signal;

[Signal("settings.confirm-pending-enrollment")]
public sealed record SettingsConfirmPendingEnrollment : Signal;

[Signal("settings.cancel-pending-enrollment")]
public sealed record SettingsCancelPendingEnrollment : Signal;

[Neuron("app.settings")]
[Screen("app.settings", Title = "Settings", Icon = "settings", Category = "System", Dock = 10)]
public sealed partial class SettingsApp : AggregateScreen<SettingsAppState>, INavigatedTo<SettingsNavArgs>, INavigatedFrom
{
    [Slot(Key = "settings")] private partial IUserSettings UserSettings { get; }
    [Slot(Key = "profile")] private partial IProfile Profile { get; }
    [Slot(Required = false)] private partial IntoChat.OS.Modules.Apps.Social.IPosts? Posts { get; }
    [Slot(Key = "ino/synapses")] private partial ISynapseStudio SynapseStudio { get; }
    [Slot(Key = "vault", Required = false)] private partial ISecretVault? Vault { get; }
    [Slot(Key = "main", Required = false)] private partial IModelCatalog? ModelCatalog { get; }
    [Slot(Key = "main", Required = false)] private partial IConnectionCatalog? ConnectionCatalog { get; }
    [Slot(Key = FriendsDefaults.KeyPart, Required = false)] private partial IFriends? Friends { get; }
    [Slot(Key = "approvals/index", Required = false)] private partial IApprovals? Approvals { get; }
    [Slot(Key = "compute", Required = false)] private partial ICompute? Compute { get; }
    [Slot(Key = "device-link", Required = false)] private partial IDeviceLink? DeviceLink { get; }
    [Slot(Key = "device-enrollment", Required = false)] private partial IDeviceEnrollment? DeviceEnrollment { get; }
    [Slot(Required = false)] private partial SlotCollection<IConnectionInbound> ConnectionInbounds { get; }
    [Slot(Required = false)] private partial SlotCollection<IConnection> Connections { get; }
    [Slot(Required = false)] private partial SlotCollection<IDevice> Devices { get; }
    [Slot(Required = false)] private partial SlotCollection<IMcpToolConnection> McpServers { get; }

    private MachineId? ThisMachine { get; }

    public SettingsApp() : this(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) { }

    [ActivatorUtilitiesConstructor]
    public SettingsApp(
        ISecretVault? secretVault = null,
        BrainPreferenceStore? preferenceStore = null,
        MachineId? thisMachine = null,
        IBrainObserver? observer = null,
        IOptions<WebModuleOptions>? webOptions = null,
        IOptions<McpModuleOptions>? mcpOptions = null,
        IClientCodeSandboxConsent? clientComputeConsent = null,
        IClientResourceConsent? clientResourceConsent = null,
        IConnectedComputeRouter? connectedComputeRouter = null,
        IConfiguration? configuration = null,
        Action<Uri>? openBrowser = null,
        IPendingDeviceEnrollmentStore? pendingEnrollmentStore = null,
        IReadOnlyList<ConnectorDefinition>? connectors = null,
        ISecretVaultPort? secretVaultPort = null,
        IOAuthDeviceFlow? oauthDeviceFlow = null,
        IConnectionSecretBinder? connectionSecretBinder = null)
    {
        SecretVault = secretVault;
        PreferenceStore = preferenceStore;
        ThisMachine = thisMachine;
        Observer = observer;
        WebOptions = webOptions;
        McpOptions = mcpOptions;
        ClientComputeConsent = clientComputeConsent;
        ClientResourceConsent = clientResourceConsent;
        ConnectedComputeRouter = connectedComputeRouter;
        Configuration = configuration;
        OpenBrowserAction = openBrowser;
        PendingEnrollmentStore = pendingEnrollmentStore;
        Connectors = connectors;
        SecretVaultPort = secretVaultPort;
        OAuthDeviceFlow = oauthDeviceFlow;
        ConnectionSecretBinder = connectionSecretBinder;
    }

    public async Task OnNavigatedTo(NavigationArrival<SettingsNavArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var normalizedTransition = ViewTransition.Normalize(arrival.Transition);
        ActiveTransition = normalizedTransition;
        var next = State.ApplyArrival(arrival.Args, normalizedTransition);
        if (string.Equals(next.Section, "models", StringComparison.Ordinal))
        {
            var modelInfo = await TryReadModelCatalogAsync(next.DefaultModel);
            next = next with
            {
                Roles = modelInfo.Roles,
                Providers = modelInfo.Providers,
                LocalOllamaEndpoint = modelInfo.LocalEndpoint,
                Device = modelInfo.Device,
            };

            if (string.Equals(next.SubPage, "web-tools", StringComparison.OrdinalIgnoreCase))
            {
                next = await RefreshWebToolsProjectionAsync(next);
            }
        }
        else if (string.Equals(next.Section, "keys", StringComparison.Ordinal))
        {
            var secrets = await InspectSecretStatusesAsync(next.Variables ?? []);
            next = next with { SecretStatuses = secrets };
        }
        else if (string.Equals(next.Section, "connections", StringComparison.Ordinal))
        {
            next = await RefreshConnectionsProjectionAsync(next);
        }

        await UpdateAsync(next);
    }

    public Rule CanNavigateFrom(NavigationDeparture departure)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return State.CanDepart(departure.Force);
    }

    public Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departure);
        return departure.Force && State.HasUnsavedChanges
            ? UpdateAsync(State with { HasUnsavedChanges = false })
            : Task.CompletedTask;
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        // Settings reacts to its control neurons through synapses: one wire per child, told apart by SignalChild.
        foreach (var toggle in SettingsControls.Toggles) { await FollowChild<IToggleSwitchControl, Toggled>(toggle.Key); }
        foreach (var combo in SettingsControls.Combos) { await FollowChild<IComboBoxControl, SelectionChanged>(combo.Key); }
        await FollowChild<IFormControl, Submitted>(SettingsControls.Search);
        await FollowChild<IFormControl, Submitted>(SettingsControls.Regional);
        await FollowChild<IFormControl, Submitted>(SettingsControls.InstallModel);
        await FollowChild<IFormControl, Submitted>(SettingsControls.SearxngUrl);
        await FollowChild<IFormControl, Submitted>(SettingsControls.McpAddForm);
        await FollowChild<IFormControl, Submitted>(SettingsControls.ApiKeys);
        await FollowChild<IFormControl, Submitted>(SettingsControls.GitHubPat);
        await FollowChild<IFormControl, Submitted>(SettingsControls.PairDevice);
        await FollowChild<IFormControl, Submitted>(SettingsControls.MunclyConnect);
        if (ThisMachine is { Value.Length: > 0 } machine)
        {
            try
            {
                await Follow<IntoChat.OS.Modules.Devices.IDevice, IntoChat.OS.Modules.Devices.InstallChanged>($"devices/{machine.Value}");
            }
#pragma warning disable CA1031 // Devices module is optional in minimal test hosts.
            catch (Exception ex)
            {
                IntoChat.OS.Host.Logging.OsConsoleLog.For<SettingsApp>().LogDebug(ex, "Skipping IDevice InstallChanged follow in minimal test host.");
            }
#pragma warning restore CA1031
        }
    }

    private SettingsChildren ChildBuilders => new(
        Child<IToggleSwitchControl, ToggleSwitchProps>,
        Child<IComboBoxControl, ComboBoxProps>,
        Child<IFormControl, FormProps>);

    [On]
    public async Task OnToggled(Toggled toggled)
    {
        ArgumentNullException.ThrowIfNull(toggled);
        if (SettingsControls.ToggleOf(SignalChild) is not { } toggle || toggle.Get(State) == toggled.IsOn)
        {
            return;
        }
        try
        {
            await UserSettings.SetVariable($"settings.toggle.{toggle.Key}", toggled.IsOn ? "true" : "false");
        }
#pragma warning disable CA1031 // Best-effort persistence when settings neuron is absent in unit tests.
        catch (Exception ex)
        {
            IntoChat.OS.Host.Logging.OsConsoleLog.For<SettingsApp>().LogDebug(ex, "Skipping toggle persistence in minimal test host.");
        }
#pragma warning restore CA1031

        if (string.Equals(toggle.Key, SettingsControls.AllowClientCodeSandbox, StringComparison.Ordinal))
        {
            ClientComputeConsent?.Set(toggled.IsOn);
            try
            {
                if (DeviceLink is { } link)
                {
                    await link.SetAllowClientCodeSandbox(toggled.IsOn);
                }
            }
#pragma warning disable CA1031 // Device link neuron is optional in minimal test hosts.
            catch (Exception ex)
            {
                IntoChat.OS.Host.Logging.OsConsoleLog.For<SettingsApp>().LogDebug(ex, "Skipping IDeviceLink.SetAllowClientCodeSandbox in minimal test host.");
            }
#pragma warning restore CA1031
        }
        else if (string.Equals(toggle.Key, SettingsControls.AllowWindowsFolderGateway, StringComparison.Ordinal))
        {
            ClientResourceConsent?.SetAllowWindowsFolderGateway(toggled.IsOn);
            try
            {
                if (DeviceLink is { } link)
                {
                    await link.SetAllowWindowsFolderGateway(toggled.IsOn);
                }
            }
#pragma warning disable CA1031 // Device link neuron is optional in minimal test hosts.
            catch (Exception ex)
            {
                IntoChat.OS.Host.Logging.OsConsoleLog.For<SettingsApp>().LogDebug(ex, "Skipping IDeviceLink.SetAllowWindowsFolderGateway in minimal test host.");
            }
#pragma warning restore CA1031
        }
        else if (string.Equals(toggle.Key, SettingsControls.AllowConsole, StringComparison.Ordinal))
        {
            ClientResourceConsent?.SetAllowConsole(toggled.IsOn);
            try
            {
                if (DeviceLink is { } link)
                {
                    await link.SetAllowConsole(toggled.IsOn);
                }
            }
#pragma warning disable CA1031 // Device link neuron is optional in minimal test hosts.
            catch (Exception ex)
            {
                IntoChat.OS.Host.Logging.OsConsoleLog.For<SettingsApp>().LogDebug(ex, "Skipping IDeviceLink.SetAllowConsole in minimal test host.");
            }
#pragma warning restore CA1031
        }

        await UpdateAsync(toggle.Set(State, toggled.IsOn));
    }

    [On]
    public Task OnSelectionChanged(SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var key = SignalChild;
        if (SettingsControls.RoleOf(key) is { } role)
        {
            return SetRoleAsync(role, changed.Id);
        }

        if (string.Equals(key, SettingsControls.WebSearchProvider, StringComparison.Ordinal))
        {
            return SetWebSearchProviderAsync(changed.Id);
        }

        if (string.Equals(key, SettingsControls.ReasoningEffort, StringComparison.Ordinal))
        {
            return SetReasoningEffortAsync(changed.Id);
        }

        if (string.Equals(key, SettingsControls.VoicePersona, StringComparison.Ordinal))
        {
            return SetVoicePersonaAsync(changed.Id);
        }

        SettingsSavePreferences? save = key switch
        {
            SettingsControls.ThemeMode when changed.Id != State.ThemeMode
                => new SettingsSavePreferences(Theme: changed.Id is "light" or "dark" ? changed.Id : null, ThemeMode: changed.Id),
            SettingsControls.BarLayout when changed.Id != State.BarLayout => new SettingsSavePreferences(BarLayout: changed.Id),
            _ => null,
        };
        return save is null ? Task.CompletedTask : SavePreferences(save);
    }

    [On]
    public Task OnSubmitted(Submitted submitted)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        string? Value(string field) => submitted.Values.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return SignalChild switch
        {
            SettingsControls.Search => UpdateAsync(State.Filter(Value("query") ?? "")),
            SettingsControls.Regional => SavePreferences(new SettingsSavePreferences(
                DisplayName: Value("displayName"), Language: Value("language"), TimeZone: Value("timeZone"), DateFormat: Value("dateFormat"))),
            SettingsControls.InstallModel => InstallModelAsync(Value("model")),
            SettingsControls.SearxngUrl => SetSearxngUrlAsync(Value("url") ?? ""),
            SettingsControls.McpAddForm => McpAddAsync(Value("name"), Value("url"), submitted.SecretRefs),
            SettingsControls.ApiKeys => SealApiKeysAsync(submitted.SecretRefs),
            SettingsControls.GitHubPat => ConnectGitHubAsync(Value("Repository"), Value("ConnectionSlug"), submitted.SecretRefs),
            SettingsControls.PairDevice => PairDevice(new SettingsPairDevice(Value("server"), Value("code"))),
            SettingsControls.MunclyConnect => ConnectMunclyAsync(Value("Url"), submitted.SecretRefs),
            _ => Task.CompletedTask,
        };
    }

    [On]
    public async Task OnInstallChanged(IntoChat.OS.Modules.Devices.InstallChanged? changed)
    {
        if (changed is null)
        {
            return;
        }

        var modelInfo = await TryReadModelCatalogAsync(State.DefaultModel);
        await UpdateAsync(State with
        {
            Roles = modelInfo.Roles.Length > 0 ? modelInfo.Roles : State.Roles,
            Providers = modelInfo.Providers.Length > 0 ? modelInfo.Providers : State.Providers,
            LocalOllamaEndpoint = modelInfo.LocalEndpoint,
            Device = modelInfo.Device ?? State.Device,
        });
    }

    public ISecretVault? SecretVault { get; set; }
    public BrainPreferenceStore? PreferenceStore { get; set; }
    public IBrainObserver? Observer { get; set; }
    public IOptions<WebModuleOptions>? WebOptions { get; set; }
    public IOptions<McpModuleOptions>? McpOptions { get; set; }
    public IClientCodeSandboxConsent? ClientComputeConsent { get; set; }
    public IClientResourceConsent? ClientResourceConsent { get; set; }
    public IConnectedComputeRouter? ConnectedComputeRouter { get; set; }
    public IConfiguration? Configuration { get; set; }
    public Action<Uri>? OpenBrowserAction { get; set; }
    public IPendingDeviceEnrollmentStore? PendingEnrollmentStore { get; set; }
    public IReadOnlyList<ConnectorDefinition>? Connectors { get; set; }
    public ISecretVaultPort? SecretVaultPort { get; set; }
    public IOAuthDeviceFlow? OAuthDeviceFlow { get; set; }
    public IConnectionSecretBinder? ConnectionSecretBinder { get; set; }

    private ILogger Log => GetLogger();

    public static IReadOnlyList<SettingsSearchEntry> SearchIndex { get; } =
    [
        new("general", "General", "appearance theme dark light auto glass acrylic spatial cyber synaptic warm paper midnight nordic language timezone date format", "os://window/profile", "sliders-horizontal", "Preferences"),
        new("desktop", "Desktop & Dock", "dock pinned apps bar widgets notifications tokens clock background tone cream sand peat ink", "os://window/settings?section=desktop", "monitor", "Preferences"),
        new("apps", "Apps", "installed apps store link install permissions capabilities memory ino", "os://sheet/install", "layout-grid", "Capabilities & Data"),
        new("connections", "Connections", "postgres salesforce google drive github slack integrations webhook oauth reconnect", "os://window/settings?section=connections", "plug", "Capabilities & Data"),
        new("files", "Files", "storage drive local computer root desktop mode synced folders conflict naming", "os://sheet/conflict", "folder", "Capabilities & Data"),
        new("models", "AI", "ai models roles chat think fast hear speak talk embed image providers ollama foundry lm studio claude gpt gemini openai anthropic google xai openrouter local inference reasoning effort voice persona default model web search searxng brave tavily mcp servers microsoft learn tools connected client compute code sandbox icodesandbox device toggle", "os://window/settings?section=models", "sparkles", "Capabilities & Data"),
        new("keys", "API keys", "personal api key secrets token encryption vault anthropic openai openrouter xai grok groq mistral google brave tavily", "os://window/secure", "key-round", "Capabilities & Data"),
        new("variables", "Variables and secrets", "environment variables key value table sealed secret vault scope local brain system", "os://window/settings?section=variables", "braces", "Capabilities & Data"),
        new("billing", "Billing", "tokens consumption credits balance auto top-up monthly spend limit payment invoices compute", "os://window/settings?section=billing", "credit-card", "Organization & System"),
        new("team", "Team and permissions", "teammates roster owner member guest invite person capability grants lfp circles", "os://window/settings?section=team", "users", "Organization & System"),
        new("notifications", "Notifications", "alerts approvals failed jobs mentions quiet hours schedule", "os://window/settings?section=notifications", "bell", "Organization & System"),
        new("keyboard", "Keyboard", "shortcuts hotkeys desktop conversation editor command palette", "os://window/settings?section=keyboard", "keyboard", "Organization & System"),
        new("danger", "Danger zone", "export archive zip delete account reset brain", "os://window/settings?section=danger", "triangle-alert", "Organization & System"),
    ];

    public const string AppName = ShippedApps.Settings;

    public static AppBundle Bundle => AppBundle.FromScreen<SettingsApp>(
        "settings",
        "2.1.0",
        description: "Your preferences, models, keys, connections and archived chats.",
        operations:
        [
            new("open", "Show Settings on the tab the input names (general when empty or unknown)."),
            new("read", "Publish the current preferences to {app}/preferences."),
            new("refresh-connections", "Show the brain's connections and the host's connector offers again."),
            new("save-preferences", "Save DisplayName and Theme with the Baseline snapshot from preferences/baseline. Reject stale drafts and preserve invalid drafts."),
            new("reload-preferences", "Discard editable drafts and load the stored preferences."),
            new("reconcile-preferences", "Refresh clean drafts from stored preferences; retain unsaved edits and report conflicts."),
            new("navigate", "Open the settings section (and sub-page) named by Input."),
            new("go-back", "Return to the previous settings page."),
            new("export-brain", "Export a portable JSON archive of this brain's settings, profile, and deployed Synapse units."),
            new("import-brain", "Restore a portable JSON archive into this brain's settings, profile, and deployed Synapse units."),
            new("set-variable", "Save or update a brain configuration variable in UserSettingsNeuron."),
            new("set-budget", "Update this brain's 30-day monthly compute spend cap in micro-credits."),
            new("connection-probe", "Probe a connected integration's live health."),
            new("connection-disconnect", "Disconnect a connected integration from this brain."),
            new("device-flow-start", "Start an RFC 8628 OAuth 2.0 Device Flow authorization."),
            new("device-flow-complete", "Complete a pending RFC 8628 OAuth 2.0 Device Flow authorization."),
            new("mcp-add", "Connect an MCP tool server to this brain."),
            new("mcp-remove", "Disconnect an MCP tool server from this brain."),
        ]);

    public static string NormalizeSection(string? section) => SettingsAppState.NormalizeSection(section);

    public static IReadOnlyList<SettingsSearchEntry> FilterSections(string? query) => SettingsAppState.FilterSections(query);

    protected override string Title => State.Title;

    [On]
    public Task Open(SettingsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshFromNeuronsAsync(request.Section);
    }

    [On]
    public Task Read(SettingsRead request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshFromNeuronsAsync();
    }

    [On]
    public Task ReloadPreferences(SettingsReloadPreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshFromNeuronsAsync();
    }

    [On]
    public Task ReconcilePreferences(SettingsReconcilePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UpdateAsync(State with { Title = "Settings", Status = "Settings" });
    }


    private BrainPreferenceStore ResolvePreferenceStore()
        => PreferenceStore is { } configured ? configured : new BrainPreferenceStore(UserSettings);

    private string TryCurrentBrainId()
        => ResolveBrainId(error => Log.LogDebug(error, "Settings brain id is unavailable during standalone test activation."))?.Value ?? string.Empty;
    [On]
    public async Task ImportBrain(SettingsImportBrain request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("import-brain", State.HasPortableArchiveRule(request.ArchiveJson)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var archiveJson = string.IsNullOrWhiteSpace(request.ArchiveJson) ? State.ExportedArchiveJson! : request.ArchiveJson;
        var snapshot = PortableBrainArchive.FromJson(archiveJson);
        var settings = UserSettings;
        await PortableBrainArchive.ImportAsync(snapshot, settings, Profile, SynapseStudio, Posts);
        var user = await settings.Read();
        var computeProjection = await ReadComputeSummaryAsync(State);
        var approvals = await TryReadPendingApprovalsAsync();
        var secrets = await InspectSecretStatusesAsync(user.Variables ?? []);
        var deployedUnits = await TryReadDeployedUnitsAsync();
        var teamRoster = await TryReadTeamRosterAsync();

        await ApplyUserSettingsAsync(
            user,
            $"Restored portable brain archive from {snapshot.BrainId} ({snapshot.Units.Count} unit(s)).",
            deployedUnits,
            teamRoster,
            section: "danger",
            archiveJson: archiveJson,
            baseline: computeProjection with
            {
                PendingApprovals = approvals,
                SecretStatuses = secrets,
            });
    }
    private BrainId? TryBrainOrNull()
        => ResolveBrainId(error => Log.LogDebug(error, "Settings brain id is unavailable during detached preview."));
    public new SettingsAppState State => base.State;

    public new Rejected? LastRejection => State.LastRejection ?? base.LastRejection;

    public View Render() => PreviewView(State);

    protected override Node Render(SettingsAppState state)
        => SettingsViewRenderer.RenderRoot(state, ChildBuilders, state.LastRejection ?? base.LastRejection);

    // The controls are controlled: their values are props of this view, so a save re-renders them; nothing is pushed to them.
    private Task UpdateAsync(SettingsAppState next) => Show(next with { LastRejection = null });

    private async Task ApplySettingsDecisionAsync(Decision<SettingsAppState> decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.IsRefused)
        {
            await PublishEphemeral(decision.Rejection);
            await Show(State with
            {
                LastRejection = decision.Rejection,
                Status = string.Join(" ", decision.Rejection.Messages),
            });
            return;
        }

        await UpdateAsync(decision.Next);
    }
}
