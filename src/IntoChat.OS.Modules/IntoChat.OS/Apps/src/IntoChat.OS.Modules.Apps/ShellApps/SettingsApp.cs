/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, sideload-list, sideload-install, sideload-add-git

The embedded folder scripts publish the Flutter settings tree, including the Modules and Apps tabs.
Sideload lists folders in the apps directory, installs one through the store path, and clones an https or ssh URL.
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

[DigitalBrain.Signal("settings.sideload-list")]
public sealed record SettingsSideloadList : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.sideload-install")]
public sealed record SettingsSideloadInstall(string? Name = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("settings.sideload-add-git")]
public sealed record SettingsSideloadAddGit(string? Url = null) : DigitalBrain.Signal;

[DigitalBrain.App("settings")]
public sealed class SettingsApp(IPresenter presenter, SideloadDesk? sideload = null) : DigitalBrain.App
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

    [DigitalBrain.On]
    public async Task SideloadList(SettingsSideloadList request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Collection([]))
            : await sideload.List(CancellationToken.None);
        Present(presenter.Present, static () => null, () => view);
    }

    [DigitalBrain.On]
    public async Task SideloadInstall(SettingsSideloadInstall request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Text(string.IsNullOrWhiteSpace(request.Name) ? "Name an app." : request.Name))
            : await sideload.Install(request.Name, CancellationToken.None);
        Present(presenter.Present, static () => null, () => view);
    }

    [DigitalBrain.On]
    public async Task SideloadAddGit(SettingsSideloadAddGit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Text(string.IsNullOrWhiteSpace(request.Url) ? "Name a git URL." : request.Url))
            : await sideload.AddGit(request.Url, CancellationToken.None);
        Present(presenter.Present, static () => null, () => view);
    }

    private Task Show(DigitalBrain.Signal request, string title)
    {
        ArgumentNullException.ThrowIfNull(request);
        Present(presenter.Present, static () => null, () => new View("Settings", new Text(title)));
        return Task.CompletedTask;
    }
}
