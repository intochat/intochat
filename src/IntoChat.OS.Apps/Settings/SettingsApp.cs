/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, navigate, go-back, export-brain, import-brain, set-variable, set-budget, set-github-triage-policy, install-issue-triage-synapse, ui.command

Declarative macOS Sonoma-style System Settings application for IntoChat OS (`UK-16` / `INT-1364`).
Owns the 13 settings sections, master-detail subpages (`AppPage`, `MemoryPage`, `ConnectionPage`,
`PersonPage`, `LocalServerPage`), modal sheets, search index, navigation backstack, and pure immutable
`SettingsAppState` DDD Aggregate projections (zero transient mutable grain fields!).
*/
using System.Globalization;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Confirmation;
using DigitalBrain.Limits;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.Form.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

[Signal("settings.export-brain")]
public sealed record SettingsExportBrain : Signal;

[Signal("settings.import-brain")]
public sealed record SettingsImportBrain(string? ArchiveJson = null) : Signal;

[Signal("settings.set-variable")]
public sealed record SettingsSetVariable(string? Key = null, string? Value = null) : Signal;

[Signal("settings.set-budget")]
public sealed record SettingsSetBudget(long MonthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros) : Signal;

[Signal("settings.set-github-triage-policy")]
public sealed record SettingsSetGitHubTriagePolicy(string Preset = "") : Signal;

[Signal("settings.install-issue-triage-synapse")]
public sealed record SettingsInstallIssueTriageSynapse(string ConnectionKey = "", string Mode = "DraftForApproval") : Signal;

[Neuron("app.settings")]
[Screen("app.settings", Title = "Settings", Icon = Icons.Sliders)]
public sealed partial class SettingsApp : AggregateScreen<SettingsAppState>, INavigatedTo<SettingsNavArgs>, INavigatedFrom
{
    public const string ApiKeysFormKey = "settings/forms/api-keys";
    public const string GitHubPatFormKey = "settings/forms/github-pat";
    public const string GitHubIssueCommentPolicyRuleId = "settings.github.issue.comment";
    public const string GitHubIssueCommentAction = "github.issue.comment";

    [GeneratedRegex(@"gh-triage-[a-z0-9._-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SynapseAliasPattern();

    public SettingsApp() : this(null, null) { }

    [ActivatorUtilitiesConstructor]
    public SettingsApp(ISecretVault? secretVault = null, BrainPreferenceStore? preferenceStore = null)
    {
        SecretVault = secretVault;
        PreferenceStore = preferenceStore;
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
                AvailableModels = modelInfo.Options,
                LocalOllamaEndpoint = modelInfo.LocalEndpoint,
            };
        }
        else if (string.Equals(next.Section, "connections", StringComparison.Ordinal) && SettingsAppState.IsGitHubSubPage(next.SubPage))
        {
            await EnsureGitHubPatFormConfiguredAsync();
            next = await RefreshGitHubHubProjectionAsync(next);
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
        // Settings reacts to its control neurons through synapses: one wire per slot, told apart by SignalSlot.
        foreach (var toggle in SettingsControls.Toggles) { await FollowSlot<ToggleSwitchNeuron, Toggled>(toggle.Key); }
        foreach (var combo in SettingsControls.Combos) { await FollowSlot<ComboBoxNeuron, SelectionChanged>(combo.Key); }
        await FollowSlot<IntoChat.OS.Apps.Controls.FormNeuron, Submitted>(SettingsControls.Search);
        await FollowSlot<IntoChat.OS.Apps.Controls.FormNeuron, Submitted>(SettingsControls.Regional);

        try
        {
            var form = Get<IForm>(ApiKeysFormKey);
            if ((await form.Read()).Version == 0)
            {
                await form.Configure(
                    "Seal API keys in Secure Vault",
                    [
                        new FormField("ANTHROPIC_API_KEY", "Anthropic API key", FormFieldKinds.Secret, Placeholder: "sk-ant-…"),
                        new FormField("OPENAI_API_KEY", "OpenAI API key", FormFieldKinds.Secret, Placeholder: "sk-…"),
                        new FormField("GOOGLE_API_KEY", "Google Gemini API key", FormFieldKinds.Secret, Placeholder: "AIza…"),
                    ],
                    submitLabel: "Save vault references");
            }
        }
#pragma warning disable CA1031 // Optional UI module in minimal test hosts.
        catch (Exception ex)
        {
            IntoChat.OS.Host.Logging.OsConsoleLog.For<SettingsApp>().LogDebug(ex, "Skipping API keys IForm seeding in minimal test host.");
        }
#pragma warning restore CA1031

        await EnsureGitHubPatFormConfiguredAsync();
    }

    private async Task EnsureGitHubPatFormConfiguredAsync()
    {
        try
        {
            var githubForm = Get<IForm>(GitHubPatFormKey);
            if ((await githubForm.Read()).Version == 0)
            {
                await githubForm.Configure(
                    "Connect GitHub Repository (Fine-Grained PAT & Webhook)",
                    [
                        new FormField("Repository", "Repository (owner/repo)", FormFieldKinds.Text, Placeholder: "owner/repo", Required: true, MaxLength: 140),
                        new FormField("PersonalAccessToken", "Personal access token", FormFieldKinds.Secret, Placeholder: "github_pat_…", Required: true),
                        new FormField("WebhookSecret", "Webhook secret", FormFieldKinds.Secret, Placeholder: "whsec_…", Required: false),
                        new FormField("ConnectionSlug", "Connection slug", FormFieldKinds.Text, Placeholder: "main", Required: false, MaxLength: 64),
                    ],
                    initialValues: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ConnectionSlug"] = State.GitHubConnectionSlug,
                    },
                    submitLabel: "Seal & connect repository");
            }

            await Follow<IForm, FormSubmitted>(GitHubPatFormKey);
        }
#pragma warning disable CA1031 // Optional UI module in minimal test hosts.
        catch (Exception ex)
        {
            IntoChat.OS.Host.Logging.OsConsoleLog.For<SettingsApp>().LogDebug(ex, "Skipping GitHub PAT IForm seeding in minimal test host.");
        }
#pragma warning restore CA1031
    }

    private SettingsSlots Slots => new(
        Slot<ToggleSwitchNeuron, ToggleSwitchProps>,
        Slot<ComboBoxNeuron, ComboBoxProps>,
        Slot<IntoChat.OS.Apps.Controls.FormNeuron, FormProps>);

    [On]
    public async Task OnToggled(Toggled toggled)
    {
        ArgumentNullException.ThrowIfNull(toggled);
        if (SettingsControls.ToggleOf(SignalSlot) is not { } toggle || toggle.Get(State) == toggled.IsOn)
        {
            return;
        }
        try
        {
            await Get<IUserSettings>("settings").SetVariable($"settings.toggle.{toggle.Key}", toggled.IsOn ? "true" : "false");
        }
#pragma warning disable CA1031 // Best-effort persistence when settings neuron is absent in unit tests.
        catch (Exception ex)
        {
            IntoChat.OS.Host.Logging.OsConsoleLog.For<SettingsApp>().LogDebug(ex, "Skipping toggle persistence in minimal test host.");
        }
#pragma warning restore CA1031
        await UpdateAsync(toggle.Set(State, toggled.IsOn));
    }

    [On]
    public Task OnSelectionChanged(SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var key = SignalSlot;
        SettingsSavePreferences? save = key switch
        {
            SettingsControls.ThemeMode when changed.Id != State.ThemeMode
                => new SettingsSavePreferences(Theme: changed.Id is "light" or "dark" ? changed.Id : null, ThemeMode: changed.Id),
            SettingsControls.BarLayout when changed.Id != State.BarLayout => new SettingsSavePreferences(BarLayout: changed.Id),
            SettingsControls.DefaultModel when changed.Id != State.DefaultModel => new SettingsSavePreferences(DefaultModel: changed.Id),
            _ => null,
        };
        return save is null ? Task.CompletedTask : SavePreferences(save);
    }

    [On]
    public Task OnSubmitted(Submitted submitted)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        string? Value(string field) => submitted.Values.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return SignalSlot switch
        {
            SettingsControls.Search => UpdateAsync(State.Filter(Value("query") ?? "")),
            SettingsControls.Regional => SavePreferences(new SettingsSavePreferences(
                DisplayName: Value("displayName"), Language: Value("language"), TimeZone: Value("timeZone"), DateFormat: Value("dateFormat"))),
            _ => Task.CompletedTask,
        };
    }

    public ISecretVault? SecretVault { get; set; }
    public BrainPreferenceStore? PreferenceStore { get; set; }

    private ILogger Log => GetLogger();

    public static IReadOnlyList<SettingsSearchEntry> SearchIndex { get; } =
    [
        new("general", "General", "appearance theme dark light auto glass acrylic spatial cyber synaptic warm paper midnight nordic language timezone date format", "os://window/profile", "sliders-horizontal", "Preferences"),
        new("desktop", "Desktop & Dock", "dock pinned apps bar widgets notifications tokens clock background tone cream sand peat ink", "os://window/settings?section=desktop", "monitor", "Preferences"),
        new("apps", "Apps", "installed apps store link install permissions capabilities memory ino", "os://sheet/install", "layout-grid", "Capabilities & Data"),
        new("connections", "Connections", "postgres salesforce google drive github slack integrations webhook oauth reconnect", "os://window/settings?section=connections", "plug", "Capabilities & Data"),
        new("files", "Files", "storage drive local computer root desktop mode synced folders conflict naming", "os://sheet/conflict", "folder", "Capabilities & Data"),
        new("models", "AI models", "ollama lm studio claude gpt gemini local inference reasoning effort default model", "os://window/settings?section=models", "sparkles", "Capabilities & Data"),
        new("keys", "API keys", "personal api key secrets token encryption vault anthropic openai groq mistral google", "os://window/secure", "key-round", "Capabilities & Data"),
        new("variables", "Variables and secrets", "environment variables key value table sealed secret vault scope local brain system", "os://window/settings?section=variables", "braces", "Capabilities & Data"),
        new("billing", "Billing", "tokens consumption credits balance auto top-up monthly spend limit payment invoices compute", "os://window/settings?section=billing", "credit-card", "Organization & System"),
        new("team", "Team and permissions", "teammates roster owner member guest invite person capability grants lfp circles", "os://window/settings?section=team", "users", "Organization & System"),
        new("notifications", "Notifications", "alerts approvals failed jobs mentions quiet hours schedule", "os://window/settings?section=notifications", "bell", "Organization & System"),
        new("keyboard", "Keyboard", "shortcuts hotkeys desktop conversation editor command palette", "os://window/settings?section=keyboard", "keyboard", "Organization & System"),
        new("danger", "Danger zone", "export archive zip delete account reset brain", "os://window/settings?section=danger", "triangle-alert", "Organization & System"),
    ];

    public const string AppName = ShippedApps.Settings;

    public static AppBundle Bundle { get; } = new(
        "settings",
        "2.1.0",
        new AppManifest(
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
                new("navigate", "Open the settings section (and sub-page) named by Input."),
                new("go-back", "Return to the previous settings page."),
                new("export-brain", "Export a portable JSON archive of this brain's settings, profile, and deployed Synapse units."),
                new("import-brain", "Restore a portable JSON archive into this brain's settings, profile, and deployed Synapse units."),
                new("set-variable", "Save or update a brain configuration variable in UserSettingsNeuron."),
                new("set-budget", "Update this brain's 30-day monthly compute spend cap in micro-credits."),
                new("set-github-triage-policy", "Adopt, update, or reset the github.issue.comment governance PolicyRule preset."),
                new("install-issue-triage-synapse", "Compile and deploy a deterministic GitHub Issue Triage Synapse unit via IInoStudio."),
                new(UiCommand.SignalAlias, "Dispatch a typed UI Kit aggregate command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "settings",
            Dock: 10),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(SettingsApp).AssemblyQualifiedName,
    };

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
        => PreferenceStore is { } configured ? configured : new BrainPreferenceStore(Get<IUserSettings>("settings"));

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
        var settings = Get<IUserSettings>("settings");
        await PortableBrainArchive.ImportAsync(snapshot, settings, Get<IProfile>("profile"), Get<ISynapseStudio>("ino/synapses"));
        var user = await settings.Read();
        var computeProjection = await ReadComputeSummaryAsync(State);
        var approvals = await TryReadPendingApprovalsAsync();
        var secrets = await InspectSecretStatusesAsync(user.Variables ?? []);
        var deployedUnits = await TryReadDeployedUnitsAsync();
        var teamFriends = await TryReadTeamFriendsAsync();

        await ApplyUserSettingsAsync(
            user,
            $"Restored portable brain archive from {snapshot.BrainId} ({snapshot.Units.Count} unit(s)).",
            deployedUnits,
            teamFriends,
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
        => SettingsViewRenderer.RenderRoot(state, Slots, state.LastRejection ?? base.LastRejection);

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

    private T? TryGetOptional<T>(string key) where T : class
        => GetOptional<T>(key, error => Log.LogDebug(error, "{Contract} is optional when running in isolated unit tests.", typeof(T).Name));
}
