using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record UserVariableEntry(string Key, string Value);

public sealed record UserSettingsState(
    string DisplayName,
    string Theme,
    string DefaultModel,
    UserVariableEntry[] Variables,
    string StylePreset = "fluent-acrylic",
    string BarLayout = "floating-glass",
    string ThemeMode = "auto",
    string DesktopTone = "cream",
    string Language = "English",
    string TimeZone = "UTC",
    string DateFormat = "17 Oct 2026")
{
    public UserSettingsState() : this("User", "dark", "qwen3.5:4b", [], "fluent-acrylic", "floating-glass", "auto", "cream", "English", "UTC", "17 Oct 2026") { }
}

[DigitalBrain.Signal("os.settings.changed")]
public sealed record UserSettingsChanged(
    string DisplayName,
    string Theme,
    string DefaultModel,
    string StylePreset = "fluent-acrylic",
    string BarLayout = "floating-glass",
    string ThemeMode = "auto",
    string DesktopTone = "cream",
    string Language = "English",
    string TimeZone = "UTC",
    string DateFormat = "17 Oct 2026") : DigitalBrain.Signal;

[NeuronAlias("os.settings"), DefaultNeuronType("os.settings")]
public interface IUserSettings
{
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<UserSettingsState> Read();

    Task<UserSettingsState> SetProfile(string displayName, string theme, string defaultModel);

    Task<UserSettingsState> SetUiIdentity(string stylePreset, string barLayout);

    Task<UserSettingsState> SetPreferences(
        string? themeMode = null,
        string? desktopTone = null,
        string? language = null,
        string? timeZone = null,
        string? dateFormat = null);

    Task<UserSettingsState> SetVariable(string key, string value);
}
