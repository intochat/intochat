using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Web;

namespace IntoChat.OS.Apps.Settings;

/// <summary>What the Settings view needs from its screen to draw a control neuron: a child with props (only the screen can render one).</summary>
internal sealed record SettingsChildren(
    Func<string, ToggleSwitchProps, Node> ToggleSwitch,
    Func<string, ComboBoxProps, Node> ComboBox,
    Func<string, FormProps, Node> Form);

/// <summary>
/// The control neurons Settings draws, by child key. Each is controlled: the app owns the preference and passes it as the control's
/// props value, the control only signals the person's change (Toggled, SelectionChanged, Submitted), and the app follows that signal,
/// stores the preference and re-renders the props.
/// </summary>
internal static class SettingsControls
{
    /// <summary>The brain variable that lists setting keys a brain admin locks (comma-separated): drawn disabled, refused at the child.</summary>
    public const string ManagedVariable = "SETTINGS_MANAGED";

    public const string AllowClientCodeSandbox = ClientComputeConsentPolicy.SettingsToggleKey;
    public const string AllowWindowsFolderGateway = ClientComputeConsentPolicy.AllowWindowsFolderGatewayToggleKey;
    public const string AllowConsole = ClientComputeConsentPolicy.AllowConsoleToggleKey;

    internal sealed record SettingSwitch(string Key, string Section, string Label, Func<SettingsAppState, bool> Get, Func<SettingsAppState, bool, SettingsAppState> Set);

    public static IReadOnlyList<SettingSwitch> Toggles { get; } =
    [
        new("bar-notifications", "desktop", "Show notifications badge in OS bar", s => s.ShowOsBarNotifications, (s, on) => s with { ShowOsBarNotifications = on }),
        new("bar-compute", "desktop", "Show live token & compute meter in OS bar", s => s.ShowOsBarComputeMeter, (s, on) => s with { ShowOsBarComputeMeter = on }),
        new("bar-clock", "desktop", "Show clock & schedule calendar in OS bar", s => s.ShowOsBarClock, (s, on) => s with { ShowOsBarClock = on }),
        new("notify-approvals", "notifications", "Needs you · ApprovalCards waiting for confirmation", s => s.NotifyApprovals, (s, on) => s with { NotifyApprovals = on }),
        new("notify-jobs", "notifications", "Background jobs & long-running agent completions", s => s.NotifyBackgroundJobs, (s, on) => s with { NotifyBackgroundJobs = on }),
        new("notify-connections", "notifications", "Connection expirations & health probe failures", s => s.NotifyConnectionHealth, (s, on) => s with { NotifyConnectionHealth = on }),
        new("quiet-hours", "notifications", "Quiet hours schedule (22:00 – 07:00)", s => s.QuietHoursEnabled, (s, on) => s with { QuietHoursEnabled = on }),
        new(AllowClientCodeSandbox, "models", "Run code on this PC", s => s.AllowClientCodeSandbox, (s, on) => s with { AllowClientCodeSandbox = on }),
        new(AllowWindowsFolderGateway, "devices", "Shared folders", s => s.AllowWindowsFolderGateway, (s, on) => s with { AllowWindowsFolderGateway = on }),
        new(AllowConsole, "devices", "Interactive shell", s => s.AllowConsole, (s, on) => s with { AllowConsole = on }),
    ];

    public const string ThemeMode = "theme-mode";
    public const string BarLayout = "bar-layout";
    public const string Search = "search";
    public const string Regional = "regional";
    public const string InstallModel = "install-model";
    public const string WebSearchProvider = "web-search-provider";
    public const string SearxngUrl = "web-searxng-url";
    public const string McpAddForm = "mcp-add";
    public const string ApiKeys = "api-keys";
    public const string GitHubPat = "github-pat";
    public const string PairDevice = "pair-device";
    public const string MunclyConnect = "muncly-connect";
    public const string ReasoningEffort = "reasoning-effort";
    public const string VoicePersona = "voice-persona";
    public const string WebSearchProviderVariable = "web.search.provider";
    public const string WebSearxngUrlVariable = "web.search.searxng-url";
    public const string ReasoningEffortVariable = "ai.reasoning.effort";
    public const string VoicePersonaVariable = "ai.voice.persona";

    public static readonly string[] SupportedReasoningEfforts = ["default", "none", "low", "medium", "high"];

    public static readonly string[] SupportedVoicePersonas =
    [
        "default", "marin", "cedar", "coral", "sage", "alloy", "verse",
        "Kore", "Puck", "Aoede", "Charon", "Fenrir",
    ];

    public static string RoleKey(ModelRole role) => $"role-{role.ToString().ToLowerInvariant()}";

    public static ModelRole? RoleOf(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        foreach (var role in Enum.GetValues<ModelRole>())
        {
            if (string.Equals(RoleKey(role), key, StringComparison.Ordinal))
            {
                return role;
            }
        }

        return null;
    }

    internal sealed record Combo(string Key, Func<SettingsAppState, string?> Get);

    public static IReadOnlyList<Combo> Combos { get; } =
    [
        new(ThemeMode, s => s.ThemeMode),
        new(BarLayout, s => s.BarLayout),
        new(WebSearchProvider, ResolveWebSearchProvider),
        new(ReasoningEffort, ResolveReasoningEffort),
        new(VoicePersona, ResolveVoicePersona),
        .. Enum.GetValues<ModelRole>().Select(role => new Combo(
            RoleKey(role),
            s => s.Roles.FirstOrDefault(r => r.Role == role)?.Selected
                ?? (role == ModelRole.Chat ? s.DefaultModel : null))),
    ];

    public static string ResolveReasoningEffort(SettingsAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var fromVariable = (state.Variables ?? [])
            .FirstOrDefault(v => string.Equals(v.Key, ReasoningEffortVariable, StringComparison.Ordinal))?.Value;
        if (TryNormalizeReasoningEffort(fromVariable) is { } varEffort)
        {
            return varEffort;
        }

        if (TryNormalizeReasoningEffort(state.ReasoningEffort) is { } stateEffort)
        {
            return stateEffort;
        }

        return "default";
    }

    public static string? TryNormalizeReasoningEffort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim().ToLowerInvariant();
        return SupportedReasoningEfforts.Contains(trimmed, StringComparer.Ordinal) ? trimmed : null;
    }

    public static string ResolveVoicePersona(SettingsAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var fromVariable = (state.Variables ?? [])
            .FirstOrDefault(v => string.Equals(v.Key, VoicePersonaVariable, StringComparison.Ordinal))?.Value;
        if (TryNormalizeVoicePersona(fromVariable) is { } varPersona)
        {
            return varPersona;
        }

        if (TryNormalizeVoicePersona(state.VoicePersona) is { } statePersona)
        {
            return statePersona;
        }

        return "default";
    }

    public static string? TryNormalizeVoicePersona(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        return SupportedVoicePersonas.FirstOrDefault(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveWebSearchProvider(SettingsAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var fromVariable = (state.Variables ?? [])
            .FirstOrDefault(v => string.Equals(v.Key, WebSearchProviderVariable, StringComparison.Ordinal))?.Value;
        if (TryNormalizeSearchProvider(fromVariable) is { } varProvider)
        {
            return varProvider;
        }

        if (TryNormalizeSearchProvider(state.WebSearchProvider) is { } stateProvider)
        {
            return stateProvider;
        }

        return nameof(SearchProvider.Off);
    }

    public static string? TryNormalizeSearchProvider(string? raw)
        => !string.IsNullOrWhiteSpace(raw)
            && Enum.TryParse<SearchProvider>(raw.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed.ToString()
            : null;

    public static string ResolveSearxngUrl(SettingsAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var fromVariable = (state.Variables ?? [])
            .FirstOrDefault(v => string.Equals(v.Key, WebSearxngUrlVariable, StringComparison.Ordinal))?.Value;
        if (!string.IsNullOrWhiteSpace(fromVariable))
        {
            return fromVariable.Trim();
        }

        return state.WebSearxngUrl?.Trim() ?? string.Empty;
    }

    public static SettingSwitch? ToggleOf(string? key) => Toggles.FirstOrDefault(toggle => toggle.Key == key);

    public static bool IsManaged(SettingsAppState state, string key)
        => (state.Variables ?? []).FirstOrDefault(variable => string.Equals(variable.Key, ManagedVariable, StringComparison.OrdinalIgnoreCase)) is { } managed
            && managed.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(key, StringComparer.Ordinal);

    public static Node ToggleSwitch(SettingsChildren children, SettingsAppState state, SettingSwitch toggle)
    {
        var managed = IsManaged(state, toggle.Key);
        return children.ToggleSwitch(toggle.Key, new ToggleSwitchProps(managed ? $"{toggle.Label} (managed)" : toggle.Label, IsEnabled: !managed, AutomationName: toggle.Label, IsOn: toggle.Get(state)));
    }

    public static Node ComboBox(SettingsChildren children, SettingsAppState state, string key, string header, IReadOnlyList<ComboBoxOption> options)
    {
        var managed = IsManaged(state, key);
        var selected = Combos.Single(combo => combo.Key == key).Get(state);
        return children.ComboBox(key, new ComboBoxProps(managed ? $"{header} (managed)" : header, options, IsEnabled: !managed, AutomationName: header, SelectedId: selected));
    }

    public static Node SearchForm(SettingsChildren children, SettingsAppState state)
        => children.Form(Search, new FormProps([new FormInput("query", "Search settings", Value: state.SearchQuery, Placeholder: "Search settings…", MaxLength: 80)], "Search", AutomationName: "Search settings"));

    public static Node RegionalForm(SettingsChildren children, SettingsAppState state)
    {
        var managed = IsManaged(state, Regional);
        return children.Form(Regional, new FormProps(
            [
                new FormInput("displayName", "Display name", Value: state.DisplayName, MaxLength: 64),
                new FormInput("language", "Language", Value: state.Language, MaxLength: 32),
                new FormInput("timeZone", "Time zone", Value: state.TimeZone, MaxLength: 64),
                new FormInput("dateFormat", "Date format", Value: state.DateFormat, MaxLength: 32),
            ],
            "Save regional settings",
            IsEnabled: !managed,
            AutomationName: "Regional settings"));
    }

    public static Node InstallModelForm(SettingsChildren children, SettingsAppState state)
    {
        var managed = IsManaged(state, InstallModel);
        return children.Form(InstallModel, new FormProps(
            [new FormInput("model", "Model ID", Placeholder: "e.g. gemma4:e2b", MaxLength: 120)],
            "Install model",
            IsEnabled: !managed,
            AutomationName: "Install a model"));
    }

    public static Node SearxngUrlForm(SettingsChildren children, SettingsAppState state)
    {
        var managed = IsManaged(state, SearxngUrl);
        return children.Form(SearxngUrl, new FormProps(
            [
                new FormInput(
                    "url",
                    "SearXNG URL",
                    Value: ResolveSearxngUrl(state),
                    Placeholder: "http://localhost:8080",
                    MaxLength: 256),
            ],
            "Save SearXNG URL",
            IsEnabled: !managed,
            AutomationName: "SearXNG URL"));
    }

    public static Node McpAddFormControl(SettingsChildren children, SettingsAppState state)
    {
        var managed = IsManaged(state, McpAddForm);
        return children.Form(McpAddForm, new FormProps(
            [
                new FormInput("name", "Server name", Value: "", Placeholder: "docs", MaxLength: 32),
                new FormInput("url", "Server URL", Value: "", Placeholder: "https://example.org/mcp", MaxLength: 512),
                new FormInput("token", "Bearer token (optional)", FormFieldKind.Secret, Placeholder: "Bearer token…"),
            ],
            "Add MCP server",
            IsEnabled: !managed,
            AutomationName: "Add MCP server"));
    }

    public static Node ApiKeysForm(SettingsChildren children, SettingsAppState state)
    {
        var managed = IsManaged(state, ApiKeys);
        return children.Form(ApiKeys, new FormProps(
            [
                new FormInput("ANTHROPIC_API_KEY", "Anthropic API key", FormFieldKind.Secret, Placeholder: "sk-ant-…"),
                new FormInput("OPENAI_API_KEY", "OpenAI API key", FormFieldKind.Secret, Placeholder: "sk-…"),
                new FormInput("GOOGLE_API_KEY", "Google Gemini API key", FormFieldKind.Secret, Placeholder: "AIza…"),
                new FormInput("OPENROUTER_API_KEY", "OpenRouter API key", FormFieldKind.Secret, Placeholder: "sk-or-…"),
                new FormInput("XAI_API_KEY", "xAI API key", FormFieldKind.Secret, Placeholder: "xai-…"),
                new FormInput("BRAVE_API_KEY", "Brave Search API key", FormFieldKind.Secret, Placeholder: "BSA…"),
                new FormInput("TAVILY_API_KEY", "Tavily API key", FormFieldKind.Secret, Placeholder: "tvly-…"),
            ],
            "Seal in vault",
            IsEnabled: !managed,
            AutomationName: "Seal API keys in Secure Vault"));
    }

    public static Node GitHubPatForm(SettingsChildren children, SettingsAppState state)
    {
        var managed = IsManaged(state, GitHubPat);
        return children.Form(GitHubPat, new FormProps(
            [
                new FormInput("Repository", "Repository (owner/repo)", Placeholder: "owner/repo", MaxLength: 140),
                new FormInput("PersonalAccessToken", "Personal access token", FormFieldKind.Secret, Placeholder: "github_pat_…"),
                new FormInput("WebhookSecret", "Webhook secret (optional)", FormFieldKind.Secret, Placeholder: "whsec_…"),
                new FormInput("ConnectionSlug", "Connection slug", Value: "main", Placeholder: "main", MaxLength: 64),
            ],
            "Seal & connect GitHub",
            IsEnabled: !managed,
            AutomationName: "Connect GitHub repository"));
    }

    public static Node PairDeviceForm(SettingsChildren children, SettingsAppState state)
    {
        var managed = IsManaged(state, PairDevice);
        return children.Form(PairDevice, new FormProps(
            [
                new FormInput("server", "Cloud server URL", Value: state.CloudLinkUrl, Placeholder: "https://intochat.io", MaxLength: 256),
                new FormInput("code", "Pairing code", Value: "", Placeholder: "ABCD-EFGH-JKMN", MaxLength: 64),
            ],
            "Link this PC",
            IsEnabled: !managed,
            AutomationName: "Enter pairing code"));
    }

    public static Node MunclyConnectForm(SettingsChildren children, SettingsAppState state)
    {
        var managed = IsManaged(state, MunclyConnect);
        return children.Form(MunclyConnect, new FormProps(
            [
                new FormInput("Url", "CRM URL", Value: SettingsApp.DefaultMunclyCrmUrl, Placeholder: SettingsApp.DefaultMunclyCrmUrl, MaxLength: 512),
                new FormInput("BearerToken", "MCP Bearer Token", FormFieldKind.Secret, Placeholder: "crm_…"),
            ],
            "Connect Muncly CRM",
            IsEnabled: !managed,
            AutomationName: "Connect Muncly CRM"));
    }
}
