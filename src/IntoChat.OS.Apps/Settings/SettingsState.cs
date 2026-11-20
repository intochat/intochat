using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Settings;

public sealed record SettingsSearchEntry(
    string SectionId,
    string Title,
    string Keywords,
    string ActionUri,
    string Icon = "settings",
    string Group = "Preferences");

public sealed record SettingsModelOption(
    string Id,
    string Label,
    bool Available = true,
    string? ProviderLabel = null,
    string? Reason = null);

/// <summary>
/// Rich DDD Aggregate owning all Settings navigation, preferences, and projected external state
/// (compute spend/cap, pending approvals, and redacted secret statuses) without transient grain fields.
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
    IReadOnlyList<string>? ConnectionKeys = null,
    IReadOnlyList<string>? FriendGrants = null,
    string? ExportedArchiveJson = null,
    IReadOnlyList<UserVariableEntry>? Variables = null,
    IReadOnlyList<string>? DeployedUnits = null,
    IReadOnlyList<ProfileFriend>? TeamFriends = null)
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

    public static IReadOnlyList<string> DefaultConnectionKeys { get; } = [];
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
    public IReadOnlyList<SettingsModelOption> AvailableModels { get; init; } = [];
    public string LocalOllamaEndpoint { get; init; } = IntoChatOSNames.LocalOllamaEndpoint;

    [JsonIgnore]
    public Rejected? LastRejection { get; init; }

    public SettingsAppState() : this(
        "Settings",
        "Settings",
        "User",
        "dark",
        ModelCatalog.DefaultChatModelId,
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
        [],
        null,
        [],
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

    [Invariant("Connection keys are non-empty and unique."), JsonIgnore]
    public bool ValidConnectionsInvariant =>
        (ConnectionKeys ?? []).All(static k => !string.IsNullOrWhiteSpace(k))
        && (ConnectionKeys ?? []).Distinct(StringComparer.Ordinal).Count() == (ConnectionKeys ?? []).Count;

    [Invariant("Stored configuration variables always carry non-empty keys."), JsonIgnore]
    public bool ValidVariablesInvariant =>
        (Variables ?? []).All(static v => !string.IsNullOrWhiteSpace(v.Key));

    // ── Declarative Domain Rules ─────────────────────────────────────────────

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
        [Sample(nameof(IQwen35))] string? defaultModel = null,
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
        var keys = (ConnectionKeys ?? DefaultConnectionKeys).ToList();
        if (!string.IsNullOrWhiteSpace(connectionKey))
        {
            var trimmedKey = connectionKey.Trim();
            if (!keys.Contains(trimmedKey, StringComparer.Ordinal))
            {
                keys.Add(trimmedKey);
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
            ConnectionKeys = keys,
            FriendGrants = grants,
        };
    }

    [IgnoreExplore]
    public SettingsAppState ApplyUserSettings(
        UserSettingsState user,
        string status,
        IReadOnlyList<string>? deployedUnits = null,
        IReadOnlyList<ProfileFriend>? teamFriends = null,
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
            Variables = (user.Variables ?? []).Where(static v => !string.IsNullOrWhiteSpace(v.Key)).ToArray(),
            DeployedUnits = deployedUnits ?? DeployedUnits ?? [],
            TeamFriends = teamFriends ?? TeamFriends ?? [],
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
            "appearance" or "profile" => "general",
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
