using DigitalBrain;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Apps;

public sealed record UserVariableEntry(string Key, string Value);

public sealed record RoleChoice(ModelRole Role, ModelId Model);

/// <summary>One notification switch of the brain's person: <c>agent-finished</c>, <c>needs-approval</c>, <c>connection-errors</c> or <c>product-updates</c>.</summary>
public sealed record NotificationSetting(string Key, bool On);

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
    string DateFormat = "17 Oct 2026",
    string ArtCraftDefaultMode = "wasm-ui",
    RoleChoice[]? Roles = null,
    NotificationSetting[]? Notifications = null,
    string? NewTabApp = null)
{
    public UserSettingsState() : this("User", "dark", "", [], "fluent-acrylic", "floating-glass", "auto", "cream", "English", "UTC", "17 Oct 2026", "wasm-ui", null, null, null) { }

    public ModelId? RoleOf(ModelRole role)
    {
        if (Roles is { Length: > 0 })
        {
            foreach (var choice in Roles)
            {
                if (choice.Role == role && !string.IsNullOrWhiteSpace(choice.Model.Value))
                {
                    return KnownModels.Find(choice.Model.Value)?.Id ?? new ModelId(choice.Model.Value.Trim());
                }
            }
        }

        if (role == ModelRole.Chat && !string.IsNullOrWhiteSpace(DefaultModel))
        {
            var trimmed = DefaultModel.Trim();
            return KnownModels.Find(trimmed)?.Id ?? new ModelId(trimmed);
        }

        return null;
    }
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
    string DateFormat = "17 Oct 2026",
    string ArtCraftDefaultMode = "wasm-ui") : DigitalBrain.Signal;

[NeuronAlias("os.settings", Formerly = ["ino.settings", "IntoChat.OS.Modules.Apps.IInoSettings"]), DefaultNeuronType("os.settings")]
public interface IUserSettings
{
    const string DefaultKey = "settings";

    static string Key(BrainId brainId) => $"{brainId.Value}/settings";

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<UserSettingsState> Read();

    Task<UserSettingsState> Open(string section, string? item = null);

    Task<UserSettingsState> SetInference(string? model = null, float? temperature = null);

    Task<UserSettingsState> SetProfile(string displayName, string theme, string defaultModel);

    Task<UserSettingsState> SetUiIdentity(string stylePreset, string barLayout);

    Task<UserSettingsState> SetPreferences(
        string? themeMode = null,
        string? desktopTone = null,
        string? language = null,
        string? timeZone = null,
        string? dateFormat = null,
        string? artCraftDefaultMode = null);

    Task<UserSettingsState> SetVariable(string key, string value);

    Task<UserSettingsState> SetRole(ModelRole role, ModelId model);

    /// <summary>Sets the given notification switches and keeps the others. <see cref="UserSettingsState.Notifications"/> is null
    /// until the first call; an unset switch keeps its default.</summary>
    Task<UserSettingsState> SetNotifications(NotificationSetting[] settings);

    /// <summary>Sets the app a new tab opens; empty means the default. <see cref="UserSettingsState.NewTabApp"/> is null until the first call.</summary>
    Task<UserSettingsState> SetNewTabApp(string app);
}
