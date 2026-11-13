using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Persists and loads user preferences cleanly through the underlying <see cref="IUserSettings"/> store.
/// </summary>
public sealed class BrainPreferenceStore(IUserSettings userSettings)
{
    private readonly IUserSettings _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));

    public Task<UserSettingsState> LoadAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return _userSettings.Read();
    }

    public async Task<UserSettingsState> SaveAsync(SettingsSavePreferences request, SettingsAppState current, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(current);

        var updated = await _userSettings.SetProfile(
            request.DisplayName ?? current.DisplayName,
            request.Theme ?? current.Theme,
            request.DefaultModel ?? current.DefaultModel);

        if (!string.IsNullOrWhiteSpace(request.StylePreset) || !string.IsNullOrWhiteSpace(request.BarLayout))
        {
            updated = await _userSettings.SetUiIdentity(
                request.StylePreset ?? current.StylePreset,
                request.BarLayout ?? current.BarLayout);
        }

        if (new[] { request.ThemeMode, request.DesktopTone, request.Language, request.TimeZone, request.DateFormat }.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            updated = await _userSettings.SetPreferences(
                request.ThemeMode ?? current.ThemeMode,
                request.DesktopTone ?? current.DesktopTone,
                request.Language ?? current.Language,
                request.TimeZone ?? current.TimeZone,
                request.DateFormat ?? current.DateFormat);
        }

        return updated;
    }

    public Task<UserSettingsState> SetVariableAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return _userSettings.SetVariable(key, value);
    }
}
