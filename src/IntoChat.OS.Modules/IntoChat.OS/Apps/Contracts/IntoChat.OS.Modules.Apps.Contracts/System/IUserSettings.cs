using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record UserVariableEntry(string Key, string Value);

public sealed record UserSettingsState(
    string DisplayName,
    string Theme,
    string DefaultModel,
    UserVariableEntry[] Variables,
    string StylePreset = "fluent-acrylic",
    string BarLayout = "floating-glass")
{
    public UserSettingsState() : this("User", "dark", "qwen3.5:4b", [], "fluent-acrylic", "floating-glass") { }
}

[DigitalBrain.Signal("os.settings.changed")]
public sealed record UserSettingsChanged(
    string DisplayName,
    string Theme,
    string DefaultModel,
    string StylePreset = "fluent-acrylic",
    string BarLayout = "floating-glass") : DigitalBrain.Signal;

[NeuronAlias("os.settings"), DefaultNeuronType("os.settings")]
public interface IUserSettings
{
    [DigitalBrain.ReadOnlyCall]
    Task<UserSettingsState> Read();

    Task<UserSettingsState> SetProfile(string displayName, string theme, string defaultModel);

    Task<UserSettingsState> SetUiIdentity(string stylePreset, string barLayout);

    Task<UserSettingsState> SetVariable(string key, string value);
}
