using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record UserVariableEntry(string Key, string Value);

public sealed record UserSettingsState(
    string DisplayName,
    string Theme,
    string DefaultModel,
    UserVariableEntry[] Variables)
{
    public UserSettingsState() : this("User", "dark", "qwen3.5:4b", []) { }
}

[DigitalBrain.Signal("os.settings.changed")]
public sealed record UserSettingsChanged(string DisplayName, string Theme, string DefaultModel) : DigitalBrain.Signal;

[NeuronAlias("os.settings"), DefaultNeuronType("os.settings")]
public interface IUserSettings
{
    [DigitalBrain.ReadOnlyCall]
    Task<UserSettingsState> Read();

    Task<UserSettingsState> SetProfile(string displayName, string theme, string defaultModel);

    Task<UserSettingsState> SetVariable(string key, string value);
}
