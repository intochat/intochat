using DigitalBrain.UI;

namespace IntoChat.OS.Apps.Settings;

/// <summary>What the Settings view needs from its screen to draw a control neuron: a slot with props (only the screen can render one).</summary>
internal sealed record SettingsSlots(
    Func<string, ToggleSwitchProps, Node> ToggleSwitch,
    Func<string, ComboBoxProps, Node> ComboBox,
    Func<string, FormProps, Node> Form);

/// <summary>
/// The control neurons Settings draws, by slot key. Each preference control is a child neuron of the app; the app follows its signal
/// (Toggled, SelectionChanged, Submitted) and stores the preference, and pushes the preference back to the control when the app's value
/// changed some other way (a save, an import, a section shown again).
/// </summary>
internal static class SettingsControls
{
    /// <summary>The brain variable that lists setting keys a brain admin locks (comma-separated): drawn disabled, refused at the slot.</summary>
    public const string ManagedVariable = "SETTINGS_MANAGED";

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
    ];

    public const string ThemeMode = "theme-mode";
    public const string BarLayout = "bar-layout";
    public const string DefaultModel = "default-model";
    public const string Search = "search";
    public const string Regional = "regional";

    internal sealed record Combo(string Key, string Section, Func<SettingsAppState, string> Get);

    public static IReadOnlyList<Combo> Combos { get; } =
    [
        new(ThemeMode, "general", s => s.ThemeMode),
        new(BarLayout, "desktop", s => s.BarLayout),
        new(DefaultModel, "models", s => s.DefaultModel),
    ];

    public static SettingSwitch? ToggleOf(string? key) => Toggles.FirstOrDefault(toggle => toggle.Key == key);

    public static bool IsManaged(SettingsAppState state, string key)
        => (state.Variables ?? []).FirstOrDefault(variable => string.Equals(variable.Key, ManagedVariable, StringComparison.OrdinalIgnoreCase)) is { } managed
            && managed.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(key, StringComparer.Ordinal);

    public static Node ToggleSwitch(SettingsSlots slots, SettingsAppState state, SettingSwitch toggle)
    {
        var managed = IsManaged(state, toggle.Key);
        return slots.ToggleSwitch(toggle.Key, new ToggleSwitchProps(managed ? $"{toggle.Label} (managed)" : toggle.Label, IsEnabled: !managed, AutomationName: toggle.Label));
    }

    public static Node ComboBox(SettingsSlots slots, SettingsAppState state, string key, string header, IReadOnlyList<ComboBoxOption> options)
    {
        var managed = IsManaged(state, key);
        return slots.ComboBox(key, new ComboBoxProps(managed ? $"{header} (managed)" : header, options, IsEnabled: !managed, AutomationName: header));
    }

    public static Node SearchForm(SettingsSlots slots, SettingsAppState state)
        => slots.Form(Search, new FormProps([new FormInput("query", "Search settings", Value: state.SearchQuery, Placeholder: "Search settings…", MaxLength: 80)], "Search", AutomationName: "Search settings"));

    public static Node RegionalForm(SettingsSlots slots, SettingsAppState state)
    {
        var managed = IsManaged(state, Regional);
        return slots.Form(Regional, new FormProps(
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
}
