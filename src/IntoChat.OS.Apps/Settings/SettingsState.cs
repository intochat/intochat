using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Devices;
using Link = IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.UI;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Apps.Settings;

public sealed record SettingsSearchEntry(
    string SectionId,
    string Title,
    string Keywords,
    string ActionUri,
    string Icon = "settings",
    string Group = "Preferences");

public sealed record SettingsModelChoice(
    string Id,
    string Label,
    bool Available = true,
    string? Reason = null);

public sealed record SettingsRoleRow(
    ModelRole Role,
    string? Selected,
    SettingsModelChoice[] Choices);

public sealed record SettingsProviderRow(
    AiProvider Provider,
    string Label,
    string Icon,
    string Status);

public sealed record McpServerRow(
    string Name,
    string Title,
    string Url,
    bool Connected,
    int ToolCount,
    string? Error = null);

public sealed record SettingsEnrolledDeviceRow(
    string MachineId,
    string DeviceName,
    bool Online,
    bool Revoked,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastHeartbeatAt,
    string[] AdvertisedCapabilities,
    string[] ConsentedFolders);

public sealed record SettingsConnectionRow(
    string Key,
    string ConnectorId,
    string ConnectionId,
    string DisplayName,
    ConnectionStatus Status,
    string? Account = null,
    int ToolCount = 0,
    string? Endpoint = null,
    DateTimeOffset? LastProbedAt = null,
    string? Detail = null);

public sealed record SettingsConnectorOffer(
    string Id,
    string DisplayName,
    string[] AuthMethods,
    string ExternalSystem);

/// <summary>
/// Rich DDD Aggregate owning all Settings navigation, preferences, and projected external state
/// (compute spend/cap, pending approvals, live connections, and redacted secret statuses) without transient grain fields.
/// </summary>
public sealed record SettingsAppState(
    string Title,
    string Status,
    string DisplayName,
    string Theme,
    string DefaultModel,
    string Section = "general",
    string? SubPage = null,
    string? Sheet = null,
    IReadOnlyList<string>? History = null,
    string SearchQuery = "",
    string StylePreset = "fluent-acrylic",
    string BarLayout = "floating-glass",
    string ThemeMode = "auto",
    string DesktopTone = "cream",
    string Language = "English",
    string TimeZone = "UTC",
    string DateFormat = "17 Oct 2026",
    IReadOnlyList<string>? FriendGrants = null,
    string? ExportedArchiveJson = null,
    IReadOnlyList<UserVariableEntry>? Variables = null,
    IReadOnlyList<string>? DeployedUnits = null)
{
    public const int MaxHistoryDepth = 20;
    public const int MaxDisplayNameLength = 64;
    public const long MinMonthlyBudgetMicros = 1_000_000L;       // $1.00
    public const long MaxMonthlyBudgetMicros = 10_000_000_000L;  // $10,000.00

    public static readonly string[] SupportedThemes = ["dark", "light"];
    public static readonly string[] SupportedThemeModes = ["auto", "dark", "light"];
    public static readonly string[] SupportedStylePresets =
    [
        "fluent-acrylic", "spatial-glass", "cyber-synaptic", "warm-paper", "midnight", "nordic",
    ];
    public static readonly string[] SupportedBarLayouts =
    [
        "floating-glass", "compact-pill", "classic-bottom", "split-top-dock",
    ];
    public static readonly string[] SupportedDesktopTones = ["cream", "sand", "peat", "ink"];

    public static IReadOnlyList<string> DefaultFriendGrants { get; } = [];

    // Projected immutable read-model fields (eliminating transient mutable grain fields on SettingsApp):
    public string ComputeSpendUsd { get; init; } = "$0.00";
    public string ComputeBudgetUsd { get; init; } = "$50.00";
    public long MonthlyBudgetMicros { get; init; } = ComputeState.DefaultMonthlyBudgetMicros;
    public bool ComputeBudgetExceeded { get; init; }
    public string ComputeRowsSummary { get; init; } = "0 model(s) metered";
    public IReadOnlyList<Approval> PendingApprovals { get; init; } = [];
    public IReadOnlyDictionary<string, bool>? SecretStatuses { get; init; }
    public bool NotifyApprovals { get; init; } = true;
    public bool NotifyBackgroundJobs { get; init; } = true;
    public bool NotifyConnectionHealth { get; init; } = true;
    public bool QuietHoursEnabled { get; init; }
    public bool ShowOsBarNotifications { get; init; } = true;
    public bool ShowOsBarComputeMeter { get; init; } = true;
    public bool ShowOsBarClock { get; init; } = true;
    public SettingsRoleRow[] Roles { get; init; } = [];
    public SettingsProviderRow[] Providers { get; init; } = [];
    public string LocalOllamaEndpoint { get; init; } = IntoChatOSNames.LocalOllamaEndpoint;
    public DeviceView? Device { get; init; }
    public Friend[]? TeamRoster { get; init; }
    public string? ActiveFriendCode { get; init; }
    public DateTimeOffset? ActiveFriendCodeExpiresAt { get; init; }
    public string? HandleLookupResult { get; init; }
    public string? TeamError { get; init; }
    public string[] FriendConnectionGrants { get; init; } = [];
    public McpServerRow[] McpServers { get; init; } = [];
    public string? WebSearchProvider { get; init; }
    public string? WebSearxngUrl { get; init; }

    // Additive navigation & catalog-driven Connections read-model fields (never raw secrets!):
    [Id(40)]
    public bool HasUnsavedChanges { get; init; }

    [Id(41)]
    public string ActiveTransition { get; init; } = string.Empty;

    [Id(55)]
    public bool AllowClientCodeSandbox { get; init; } = ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform();

    [Id(56)]
    public string? ReasoningEffort { get; init; }

    [Id(57)]
    public string? VoicePersona { get; init; }

    // Option A (1-click pairing) & Mode B (PC resource relay consent) read-model fields (never raw secrets/codes persisted!):
    [Id(58)]
    public bool IsCloudTopology { get; init; }

    [Id(59)]
    public string CloudLinkUrl { get; init; } = Link.DeviceEnrollUri.DefaultCloudUrl;

    [Id(60)]
    public Link.LinkState DeviceLinkState { get; init; } = Link.LinkState.NotJoined;

    [Id(61)]
    public string? DeviceLinkServer { get; init; }

    [Id(62)]
    public string? DeviceLinkCircleId { get; init; }

    [Id(63)]
    public DateTimeOffset? DeviceLinkLastContactedAt { get; init; }

    [Id(64)]
    public string[] DeviceLinkCapabilities { get; init; } = [];

    [Id(65)]
    public bool AllowWindowsFolderGateway { get; init; }

    [Id(66)]
    public bool AllowConsole { get; init; }

    [Id(67)]
    public bool ShowManualPairingForm { get; init; }

    [Id(68)]
    public string? DevicePairingFeedback { get; init; }

    [Id(69)]
    public bool DevicePairingFeedbackIsError { get; init; }

    [Id(70)]
    public SettingsEnrolledDeviceRow[] EnrolledDevices { get; init; } = [];

    [Id(71)]
    public SettingsConnectionRow[] ActiveConnections { get; init; } = [];

    [Id(72)]
    public SettingsConnectorOffer[] AvailableConnectors { get; init; } = [];

    [Id(73)]
    public string WebhookBaseUrl { get; init; } = SettingsApp.DefaultWebhookBaseUrl;

    [Id(74)]
    public bool AllowConnectedEngineering { get; init; }

    [JsonIgnore]
    public string? DeviceFlowId { get; init; }

    [JsonIgnore]
    public string? DeviceFlowUserCode { get; init; }

    [JsonIgnore]
    public string? DeviceFlowVerificationUri { get; init; }

    [JsonIgnore]
    public string? DeviceFlowStatus { get; init; }

    [JsonIgnore]
    public string? MintedEnrollmentCode { get; init; }

    [JsonIgnore]
    public DateTimeOffset? MintedEnrollmentExpiresAt { get; init; }

    [JsonIgnore]
    public string? MintedEnrollmentHandoffUri { get; init; }

    [JsonIgnore]
    public string? PendingEnrollmentServerOrigin { get; init; }

    [JsonIgnore]
    public DateTimeOffset? PendingEnrollmentExpiresAt { get; init; }

    [JsonIgnore]
    public Rejected? LastRejection { get; init; }

    public SettingsAppState() : this(
        "Settings",
        "Settings",
        "User",
        "dark",
        KnownModels.Defaults.Device[ModelRole.Chat].Value,
        "general",
        null,
        null,
        [],
        "",
        "fluent-acrylic",
        "floating-glass",
        "auto",
        "cream",
        "English",
        "UTC",
        "17 Oct 2026",
        [],
        null,
        [],
        [])
    {
    }

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("Active section is always one of the 13 canonical Settings sections."), JsonIgnore]
    public bool ValidSectionInvariant => SettingsApp.SearchIndex.Any(entry => entry.SectionId == Section);

    [Invariant("Monthly compute budget cap is always positive and within platform bounds."), JsonIgnore]
    public Rule ValidBudgetCapInvariant => ValidateBudget(MonthlyBudgetMicros);

    [Invariant("Navigation history never exceeds 20 entries."), JsonIgnore]
    public bool BoundedHistoryInvariant => (History?.Count ?? 0) <= MaxHistoryDepth;

    [Invariant("Display name is non-empty within bounds and theme/appearance tokens are valid."), JsonIgnore]
    public bool ValidProfileAndThemeInvariant =>
        !string.IsNullOrWhiteSpace(DisplayName)
        && DisplayName.Length <= MaxDisplayNameLength
        && SupportedThemes.Contains(Theme, StringComparer.Ordinal)
        && SupportedThemeModes.Contains(ThemeMode, StringComparer.Ordinal)
        && !string.IsNullOrWhiteSpace(StylePreset)
        && !string.IsNullOrWhiteSpace(BarLayout)
        && !string.IsNullOrWhiteSpace(DesktopTone);

    [Invariant("Active connection keys are non-empty and unique."), JsonIgnore]
    public bool ValidConnectionsInvariant =>
        (ActiveConnections ?? []).All(static c => !string.IsNullOrWhiteSpace(c.Key))
        && (ActiveConnections ?? []).Select(static c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == (ActiveConnections ?? []).Length;

    [Invariant("Stored configuration variables always carry non-empty keys."), JsonIgnore]
    public bool ValidVariablesInvariant =>
        (Variables ?? []).All(static v => !string.IsNullOrWhiteSpace(v.Key));

    [Invariant("Navigation transition token remains valid."), JsonIgnore]
    public bool ValidTransitionInvariant => ViewTransition.IsValid(ActiveTransition);

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public Rule CanDepart(bool force = false) => Decision.Rule(
        "settings_unsaved_changes",
        (HasUnsavedChanges, Force: force),
        static x => x.HasUnsavedChanges && !x.Force,
        "Save or discard unsaved settings changes before leaving Settings.");

    public static Rule ValidateGitHubRepositoryRule(string? repository) => Decision.Rule(
        "invalid_github_repository",
        repository,
        static r => NormalizeGitHubRepository(r) is null,
        "GitHub repository must be formatted as 'owner/repo'.");

    [JsonIgnore]
    public bool CanGoBack => !string.IsNullOrEmpty(SubPage) || !string.IsNullOrEmpty(Sheet) || (History is { Count: > 0 });

    [JsonIgnore]
    public Rule CanReturnRule => Decision.Require(
        "can_go_back",
        this,
        static s => s.CanGoBack,
        "No previous settings page or sheet to return to.");

    public static Rule SupportedSectionRule(string? section) => Decision.Rule(
        "invalid_section",
        section,
        static s => TryResolveSectionStrict(s) is null,
        static s => $"Unknown settings section '{s ?? "(empty)"}'.");

    public static Rule ValidDisplayNameRule(string? displayName) => Decision.Rule(
        "invalid_display_name",
        displayName,
        static name => name is not null && (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxDisplayNameLength),
        $"Display name must be non-empty and at most {MaxDisplayNameLength} characters.");

    public static Rule ValidThemeRule(string? theme) => Decision.Rule(
        "invalid_theme",
        theme,
        static t => t is not null && !SupportedThemes.Contains(t.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Theme must be 'dark' or 'light'.");

    public static Rule ValidThemeModeRule(string? themeMode) => Decision.Rule(
        "invalid_theme_mode",
        themeMode,
        static m => m is not null && !SupportedThemeModes.Contains(m.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Theme mode must be 'auto', 'dark', or 'light'.");

    public static Rule ValidStylePresetRule(string? stylePreset) => Decision.Rule(
        "invalid_style_preset",
        stylePreset,
        static p => p is not null && !SupportedStylePresets.Contains(p.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Unsupported UI style preset.");

    public static Rule ValidBarLayoutRule(string? barLayout) => Decision.Rule(
        "invalid_bar_layout",
        barLayout,
        static b => b is not null && !SupportedBarLayouts.Contains(b.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Unsupported OS bar layout.");

    public static Rule ValidDesktopToneRule(string? desktopTone) => Decision.Rule(
        "invalid_desktop_tone",
        desktopTone,
        static tone => tone is not null && !SupportedDesktopTones.Contains(tone.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Desktop tone must be 'cream', 'sand', 'peat', or 'ink'.");

    public static Rule ValidateBudget(long monthlyBudgetMicros) => Decision.Rule(
        "valid_monthly_budget",
        monthlyBudgetMicros,
        static b => b < MinMonthlyBudgetMicros || b > MaxMonthlyBudgetMicros,
        static b => $"Monthly compute cap must be between $1.00 and $10,000.00 (received {b} micro-credits).");

    public static Rule ValidateVariableKey(string? key) => Decision.Rule(
        "variable_key_required",
        key,
        static k => string.IsNullOrWhiteSpace(k) || k.Trim().Any(char.IsWhiteSpace),
        "Variable key must be a non-empty identifier without whitespace.");

    public static Rule ValidateConnectionKey(string? connectionKey) => Decision.Rule(
        "invalid_connection_key",
        connectionKey,
        static k => string.IsNullOrWhiteSpace(k),
        "Connection key cannot be empty.");

    public static Rule ValidateMcpServerNameRule(string? name) => Decision.Rule(
        "invalid_mcp_server_name",
        name,
        static n => string.IsNullOrWhiteSpace(n) || !IsValidMcpServerName(n.Trim().ToLowerInvariant()),
        "MCP server name must match ^[a-z0-9][a-z0-9-]{0,31}$.");

    public static Rule ValidateMcpServerUrlRule(string? url) => Decision.Rule(
        "invalid_mcp_server_url",
        url,
        static u => !IsAllowedMcpServerUrl(u),
        "MCP server URL must be an absolute https URL (http is allowed only for loopback) without credentials.");

    public static Rule ValidateSearxngUrlRule(string? url) => Decision.Rule(
        "invalid_searxng_url",
        url,
        static u => !string.IsNullOrWhiteSpace(u) && (!Uri.TryCreate(u.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")),
        "SearXNG URL must be an absolute http or https URL.");

    private static bool IsValidMcpServerName(string value)
    {
        if (value.Length is 0 or > 32)
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(value[0]) && !char.IsAsciiDigit(value[0]))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllowedMcpServerUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }

    public Rule HasPortableArchiveRule(string? archiveJson) => Decision.Rule(
        "archive_required",
        (Provided: archiveJson, Cached: ExportedArchiveJson),
        static x => string.IsNullOrWhiteSpace(x.Provided) && string.IsNullOrWhiteSpace(x.Cached),
        "No portable brain archive JSON provided to import.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public SettingsAppState Navigate(
        [Sample("general", "models", "billing", "apps:ino")] string? section,
        [Sample("", "local-server")] string? subPage = null,
        [Sample("")] string? sheet = null,
        [Sample("")] string? searchQuery = null)
        => NavigateCore(section, subPage, sheet, searchQuery);

    public Decision<SettingsAppState> OpenSection(
        [Sample("connections", "invalid-section")] string? section,
        [Sample("")] string? subPage = null)
    {
        if (Decision.Check("open-section", SupportedSectionRule(section)) is { } rejected)
        {
            return rejected;
        }

        return Decision.Accept(NavigateCore(section, subPage, rawSheet: null, searchQuery: null));
    }

    public Decision<SettingsAppState> ReturnPrevious()
    {
        if (Decision.Check("go-back", CanReturnRule) is { } rejected)
        {
            return rejected;
        }

        return Decision.Accept(GoBack());
    }

    [IgnoreExplore]
    public SettingsAppState GoBack()
    {
        var history = (History ?? []).ToList();
        if (history.Count == 0)
        {
            return this with { Sheet = null, SubPage = null };
        }

        var last = history[^1];
        history.RemoveAt(history.Count - 1);
        var parts = last.Split('/', 2);
        var (section, subPage) = (parts[0], parts.Length > 1 ? parts[1] : null);
        return this with
        {
            Title = $"Settings · {section}",
            Status = $"Back to {section}" + (subPage != null ? $" / {subPage}" : ""),
            Section = section,
            SubPage = subPage,
            Sheet = null,
            History = history,
        };
    }

    public SettingsAppState Filter([Sample("", "Ollama")] string query)
        => this with
        {
            SearchQuery = query?.Trim() ?? string.Empty,
            Status = string.IsNullOrWhiteSpace(query)
                ? $"Showing all {SettingsApp.SearchIndex.Count} sections"
                : $"Filtered sections matching '{query.Trim()}'",
        };

    public Decision<SettingsAppState> ApplyPreferences(
        [Sample("Ada", "")] string? displayName = null,
        [Sample("dark", "neon-invalid")] string? theme = null,
        [Sample(nameof(KnownModels.Qwen35))] string? defaultModel = null,
        [Sample("fluent-acrylic")] string? stylePreset = null,
        [Sample("floating-glass")] string? barLayout = null,
        [Sample("auto")] string? themeMode = null,
        [Sample("cream")] string? desktopTone = null)
    {
        if (Decision.Check(
                "apply-preferences",
                ValidDisplayNameRule(displayName),
                ValidThemeRule(theme),
                ValidStylePresetRule(stylePreset),
                ValidBarLayoutRule(barLayout),
                ValidThemeModeRule(themeMode),
                ValidDesktopToneRule(desktopTone)) is { } rejected)
        {
            return rejected;
        }

        var nextName = displayName is null ? DisplayName : displayName.Trim();
        var nextTheme = theme is null ? Theme : theme.Trim().ToLowerInvariant();
        var nextModel = string.IsNullOrWhiteSpace(defaultModel) ? DefaultModel : defaultModel.Trim();
        var nextPreset = stylePreset is null ? StylePreset : stylePreset.Trim().ToLowerInvariant();
        var nextBar = barLayout is null ? BarLayout : barLayout.Trim().ToLowerInvariant();
        var nextMode = themeMode is null ? ThemeMode : themeMode.Trim().ToLowerInvariant();
        var nextTone = desktopTone is null ? DesktopTone : desktopTone.Trim().ToLowerInvariant();

        return Decision.Accept(this with
        {
            Title = "Settings",
            Status = $"Saved: {nextName} ({nextTheme}, {nextModel}, {nextPreset}, {nextBar})",
            DisplayName = nextName,
            Theme = nextTheme,
            DefaultModel = nextModel,
            StylePreset = nextPreset,
            BarLayout = nextBar,
            ThemeMode = nextMode,
            DesktopTone = nextTone,
            HasUnsavedChanges = false,
        });
    }

    public Decision<SettingsAppState> ApplyBudgetCap([Sample(50_000_000L, 0L)] long monthlyBudgetMicros)
    {
        if (Decision.Check("set-budget", ValidateBudget(monthlyBudgetMicros)) is { } rejected)
        {
            return rejected;
        }

        var usd = FormatUsd(monthlyBudgetMicros);
        return Decision.Accept(this with
        {
            Section = "billing",
            MonthlyBudgetMicros = monthlyBudgetMicros,
            ComputeBudgetUsd = usd,
            Status = $"Updated monthly compute cap to {usd}.",
        });
    }

    public Decision<SettingsAppState> RegisterConnection(
        [Sample("github", "")] string? connectionKey,
        [Sample("")] string? friendApp = null)
    {
        if (Decision.Check("register-connection", ValidateConnectionKey(connectionKey)) is { } rejected)
        {
            return rejected;
        }

        return Decision.Accept(RefreshConnectionsProjection(connectionKey!.Trim(), friendApp));
    }

    public Decision<SettingsAppState> UpsertVariable(
        [Sample("OLLAMA_HOST", "")] string? key,
        [Sample("http://127.0.0.1:11434")] string? value = "")
    {
        if (Decision.Check("set-variable", ValidateVariableKey(key)) is { } rejected)
        {
            return rejected;
        }

        var cleanKey = key!.Trim();
        var cleanVal = value ?? string.Empty;
        var existing = (Variables ?? []).ToList();
        var idx = existing.FindIndex(v => string.Equals(v.Key, cleanKey, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            existing[idx] = new UserVariableEntry(cleanKey, cleanVal);
        }
        else
        {
            existing.Add(new UserVariableEntry(cleanKey, cleanVal));
        }

        return Decision.Accept(ApplyStoredVariables(cleanKey, existing));
    }

    // ── Screen & External Neuron Projections ─────────────────────────────────

    [IgnoreExplore]
    public SettingsAppState RefreshConnectionsProjection(string? connectionKey, string? friendApp)
    {
        var active = (ActiveConnections ?? []).ToList();
        if (!string.IsNullOrWhiteSpace(connectionKey))
        {
            var trimmedKey = connectionKey.Trim();
            if (!active.Any(c => string.Equals(c.Key, trimmedKey, StringComparison.OrdinalIgnoreCase)))
            {
                var (connectorId, connectionId) = ParseConnectionKeyParts(trimmedKey);
                active.Add(new SettingsConnectionRow(
                    Key: trimmedKey,
                    ConnectorId: connectorId,
                    ConnectionId: connectionId,
                    DisplayName: trimmedKey,
                    Status: ConnectionStatus.Connected));
            }
        }

        var grants = (FriendGrants ?? DefaultFriendGrants).ToList();
        if (!string.IsNullOrWhiteSpace(friendApp))
        {
            var cleanKey = string.IsNullOrWhiteSpace(connectionKey) ? "storage@1" : connectionKey.Trim();
            var grantEntry = $"sdk -> {friendApp.Trim()} ({cleanKey})";
            if (!grants.Contains(grantEntry, StringComparer.Ordinal))
            {
                grants.Add(grantEntry);
            }
        }

        return this with
        {
            Title = "Settings",
            Status = "Connections",
            ActiveConnections = [.. active.OrderBy(static c => c.Key, StringComparer.OrdinalIgnoreCase)],
            FriendGrants = grants,
        };
    }

    private static (string ConnectorId, string ConnectionId) ParseConnectionKeyParts(string key)
    {
        var normalized = key.Trim().Trim('/');
        if (normalized.Contains("/connection/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = normalized.IndexOf("/connection/", StringComparison.OrdinalIgnoreCase);
            normalized = normalized[(idx + "/connection/".Length)..];
        }
        else if (normalized.StartsWith("connection/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["connection/".Length..];
        }

        var parts = normalized.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (normalized, "main");
    }

    [IgnoreExplore]
    public SettingsAppState ApplyUserSettings(
        UserSettingsState user,
        string status,
        IReadOnlyList<string>? deployedUnits = null,
        Friend[]? teamRoster = null,
        string? section = null,
        string? archiveJson = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var trimmedDisplayName = user.DisplayName?.Trim();
        var cleanDisplayName = string.IsNullOrWhiteSpace(trimmedDisplayName)
            ? DisplayName
            : trimmedDisplayName.Length <= MaxDisplayNameLength ? trimmedDisplayName : trimmedDisplayName[..MaxDisplayNameLength];
        var cleanTheme = !string.IsNullOrWhiteSpace(user.Theme) && SupportedThemes.Contains(user.Theme.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            ? user.Theme.Trim().ToLowerInvariant()
            : Theme;
        var cleanThemeMode = !string.IsNullOrWhiteSpace(user.ThemeMode) && SupportedThemeModes.Contains(user.ThemeMode.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            ? user.ThemeMode.Trim().ToLowerInvariant()
            : ThemeMode;
        var storedVariables = (user.Variables ?? []).Where(static v => !string.IsNullOrWhiteSpace(v.Key)).ToArray();
        var allowClientCodeSandbox = ResolveStoredBool(
            storedVariables,
            ClientComputeConsentPolicy.SettingsVariableKey,
            AllowClientCodeSandbox);
        var allowWindowsFolderGateway = ResolveStoredBool(
            storedVariables,
            ClientComputeConsentPolicy.AllowWindowsFolderGatewayVariableKey,
            AllowWindowsFolderGateway);
        var allowConsole = ResolveStoredBool(
            storedVariables,
            ClientComputeConsentPolicy.AllowConsoleVariableKey,
            AllowConsole);
        var allowConnectedEngineering = ResolveStoredBool(
            storedVariables,
            ClientComputeConsentPolicy.AllowConnectedEngineeringVariableKey,
            AllowConnectedEngineering);
        var reasoningEffort = SettingsControls.TryNormalizeReasoningEffort(
            storedVariables.FirstOrDefault(v => string.Equals(v.Key, SettingsControls.ReasoningEffortVariable, StringComparison.Ordinal))?.Value)
            ?? ReasoningEffort;
        var voicePersona = SettingsControls.TryNormalizeVoicePersona(
            storedVariables.FirstOrDefault(v => string.Equals(v.Key, SettingsControls.VoicePersonaVariable, StringComparison.Ordinal))?.Value)
            ?? VoicePersona;

        return this with
        {
            Title = "Settings",
            Status = status,
            DisplayName = cleanDisplayName,
            Theme = cleanTheme,
            DefaultModel = string.IsNullOrWhiteSpace(user.DefaultModel) ? DefaultModel : user.DefaultModel,
            Section = section is null ? Section : NormalizeSection(section),
            StylePreset = string.IsNullOrWhiteSpace(user.StylePreset) ? StylePreset : user.StylePreset,
            BarLayout = string.IsNullOrWhiteSpace(user.BarLayout) ? BarLayout : user.BarLayout,
            ThemeMode = cleanThemeMode,
            DesktopTone = string.IsNullOrWhiteSpace(user.DesktopTone) ? DesktopTone : user.DesktopTone,
            Language = string.IsNullOrWhiteSpace(user.Language) ? Language : user.Language,
            TimeZone = string.IsNullOrWhiteSpace(user.TimeZone) ? TimeZone : user.TimeZone,
            DateFormat = string.IsNullOrWhiteSpace(user.DateFormat) ? DateFormat : user.DateFormat,
            ExportedArchiveJson = archiveJson ?? ExportedArchiveJson,
            Variables = storedVariables,
            DeployedUnits = deployedUnits ?? DeployedUnits ?? [],
            TeamRoster = teamRoster ?? TeamRoster,
            AllowClientCodeSandbox = allowClientCodeSandbox,
            AllowWindowsFolderGateway = allowWindowsFolderGateway,
            AllowConsole = allowConsole,
            AllowConnectedEngineering = allowConnectedEngineering,
            ReasoningEffort = reasoningEffort,
            VoicePersona = voicePersona,
            HasUnsavedChanges = false,
        };
    }

    private static bool ResolveStoredBool(IReadOnlyList<UserVariableEntry> variables, string key, bool fallback)
    {
        var raw = variables.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal))?.Value;
        return bool.TryParse(raw?.Trim(), out var parsed) ? parsed : fallback;
    }

    [IgnoreExplore]
    public SettingsAppState MarkUnsavedChanges(bool unsaved = true)
        => this with { HasUnsavedChanges = unsaved };

    [IgnoreExplore]
    public SettingsAppState ApplyArrival(SettingsNavArgs? args, string transition = ViewTransition.FadeThrough)
    {
        var normalizedTransition = ViewTransition.Normalize(transition);
        if (args is null)
        {
            return this with { ActiveTransition = normalizedTransition };
        }

        var rawSection = args.Section;
        var rawSubPage = args.SubPage;
        if (!string.IsNullOrWhiteSpace(rawSection)
            && rawSection.Contains('/', StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(rawSubPage))
        {
            var slashParts = rawSection.Split('/', 2, StringSplitOptions.TrimEntries);
            rawSection = slashParts[0];
            rawSubPage = slashParts.Length > 1 && !string.IsNullOrWhiteSpace(slashParts[1]) ? slashParts[1] : null;
        }

        var navigated = NavigateCore(rawSection, rawSubPage, args.Sheet, args.SearchQuery);
        return navigated with
        {
            ActiveTransition = normalizedTransition,
            SearchQuery = args.SearchQuery is not null ? args.SearchQuery.Trim() : navigated.SearchQuery,
        };
    }

    [IgnoreExplore]
    public SettingsAppState ApplyStoredVariables(string key, IReadOnlyList<UserVariableEntry> updatedVariables) =>
        this with
        {
            Section = "variables",
            Status = $"Saved variable {key}.",
            Variables = [.. updatedVariables.Where(static v => !string.IsNullOrWhiteSpace(v.Key))],
        };

    [IgnoreExplore]
    public SettingsAppState RecordExportedArchive(string archiveJson, int unitCount) =>
        this with
        {
            Section = "danger",
            Sheet = "export-archive",
            Status = $"Exported portable brain archive ({unitCount} unit(s)).",
            ExportedArchiveJson = archiveJson,
        };

    // ── Normalization, Search & Secret Redaction Utilities ───────────────────

    public static string? TryResolveSectionStrict(string? rawSection)
    {
        if (string.IsNullOrWhiteSpace(rawSection))
        {
            return null;
        }

        var token = rawSection.Contains(':', StringComparison.Ordinal)
            ? rawSection.Split(':', 2, StringSplitOptions.TrimEntries)[0]
            : rawSection.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return token.ToLowerInvariant() switch
        {
            "appearance" or "profile" or "account" or "devices" => "general",
            "compute" => "billing",
            "circles" or "friends" => "team",
            "modules" => "apps",
            "archived" => "files",
            var s when SettingsApp.SearchIndex.Any(entry => entry.SectionId == s) => s,
            _ => null,
        };
    }

    public static string NormalizeSection(string? section)
        => TryResolveSectionStrict(section) ?? "general";

    public static IReadOnlyList<SettingsSearchEntry> FilterSections(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SettingsApp.SearchIndex;
        }

        var q = query.Trim();
        return
        [
            .. SettingsApp.SearchIndex.Where(entry =>
                entry.SectionId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || entry.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || entry.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    public static bool IsSecretKey(string key) =>
        key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_PASSWORD", StringComparison.OrdinalIgnoreCase);

    public static string RedactSecretDisplay(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Not set";
        }

        if (IsSecretKey(key))
        {
            return "Configured";
        }

        return value;
    }

    public static string FormatUsd(long micros)
        => (micros / 1_000_000.0).ToString("$0.00", CultureInfo.InvariantCulture);

    public static string NormalizeConnectionSlug(string? rawSlug, string fallback = "main")
    {
        if (string.IsNullOrWhiteSpace(rawSlug))
        {
            return fallback;
        }

        var trimmed = rawSlug.Trim().Trim('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash >= 0 && slash < trimmed.Length - 1)
        {
            trimmed = trimmed[(slash + 1)..];
        }

        var chars = trimmed.Where(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray();
        return chars.Length > 0 ? new string(chars).ToLowerInvariant() : fallback;
    }

    public static string? NormalizeGitHubRepository(string? rawRepository)
    {
        if (string.IsNullOrWhiteSpace(rawRepository))
        {
            return null;
        }

        var trimmed = rawRepository.Trim();
        if (trimmed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["https://github.com/".Length..];
        }
        else if (trimmed.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["http://github.com/".Length..];
        }

        trimmed = trimmed.Trim('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
        {
            trimmed = trimmed[..^4];
        }

        var parts = trimmed.Split('/');
        if (parts.Length != 2)
        {
            return null;
        }

        var owner = parts[0].Trim();
        var repo = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(repo)
            || !IsValidRepoSegment(owner)
            || !IsValidRepoSegment(repo))
        {
            return null;
        }

        return $"{owner}/{repo}";

        static bool IsValidRepoSegment(string segment)
            => segment.Any(char.IsLetterOrDigit)
                && segment.All(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }

    public override string ToString()
        => $"{Section}{(SubPage is null ? "" : "/" + SubPage)}{(Sheet is null ? "" : "#" + Sheet)} · {ComputeBudgetUsd}";

    private SettingsAppState NavigateCore(
        string? rawSection,
        string? rawSubPage,
        string? rawSheet,
        string? searchQuery)
    {
        if (!string.IsNullOrWhiteSpace(rawSection)
            && rawSection.Contains(':', StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(rawSubPage)
            && string.IsNullOrWhiteSpace(rawSheet))
        {
            var segments = rawSection.Split(':', 3, StringSplitOptions.TrimEntries);
            rawSection = segments[0];
            rawSubPage = segments.Length > 1 && !string.IsNullOrWhiteSpace(segments[1]) ? segments[1] : null;
            rawSheet = segments.Length > 2 && !string.IsNullOrWhiteSpace(segments[2]) ? segments[2] : null;
        }

        var requestedToken = rawSection?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(rawSubPage) && requestedToken is "devices" or "account")
        {
            rawSubPage = "devices";
        }

        var targetSection = string.IsNullOrWhiteSpace(rawSection) ? Section : NormalizeSection(rawSection);
        var targetSubPage = string.IsNullOrWhiteSpace(rawSubPage) ? null : rawSubPage.Trim();
        var targetSheet = string.IsNullOrWhiteSpace(rawSheet) ? null : rawSheet.Trim();

        var history = (History ?? []).ToList();
        var currentPath = string.IsNullOrEmpty(SubPage) ? Section : $"{Section}/{SubPage}";
        var nextPath = string.IsNullOrEmpty(targetSubPage) ? targetSection : $"{targetSection}/{targetSubPage}";
        if (currentPath != nextPath && !string.IsNullOrEmpty(currentPath))
        {
            history.Add(currentPath);
            if (history.Count > MaxHistoryDepth)
            {
                history.RemoveAt(0);
            }
        }

        return this with
        {
            Title = $"Settings · {targetSection}",
            Status = $"Navigated to {targetSection}" + (targetSubPage != null ? $" / {targetSubPage}" : ""),
            Section = targetSection,
            SubPage = targetSubPage,
            Sheet = targetSheet,
            SearchQuery = string.IsNullOrEmpty(searchQuery) ? SearchQuery : searchQuery,
            History = history,
        };
    }
}
