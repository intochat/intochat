/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences

The embedded folder scripts publish the Flutter settings tree, including the Modules and Apps tabs.
These operations present a status for the baseline. Empty input records no error.
*/
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("settings.open")]
public sealed record SettingsOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.read")]
public sealed record SettingsRead : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.refresh-connections")]
public sealed record SettingsRefreshConnections : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.save-preferences")]
public sealed record SettingsSavePreferences : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.reload-preferences")]
public sealed record SettingsReloadPreferences : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.reconcile-preferences")]
public sealed record SettingsReconcilePreferences : DigitalBrain.Signal;

[DigitalBrain.App("settings")]
public sealed class SettingsApp(IPresenter presenter) : DigitalBrain.App
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "settings", "Settings", "2.0.1", [], new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = ShellPrograms.Load("Settings");

    [DigitalBrain.On]
    public Task Open(SettingsOpen request) => Show(request, "Settings");

    [DigitalBrain.On]
    public Task Read(SettingsRead request) => Show(request, "Settings");

    [DigitalBrain.On]
    public Task RefreshConnections(SettingsRefreshConnections request) => Show(request, "Connections");

    [DigitalBrain.On]
    public Task SavePreferences(SettingsSavePreferences request) => Show(request, "Nothing to save.");

    [DigitalBrain.On]
    public Task ReloadPreferences(SettingsReloadPreferences request) => Show(request, "Settings");

    [DigitalBrain.On]
    public Task ReconcilePreferences(SettingsReconcilePreferences request) => Show(request, "Settings");

    private Task Show(DigitalBrain.Signal request, string title)
    {
        ArgumentNullException.ThrowIfNull(request);
        Present(presenter.Present, static () => null, () => new View("Settings", new Text(title)));
        return Task.CompletedTask;
    }
}
