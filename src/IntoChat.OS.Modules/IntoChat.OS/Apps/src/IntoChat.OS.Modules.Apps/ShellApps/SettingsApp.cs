/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, sideload-list, sideload-install, sideload-add-git

The embedded folder scripts publish the Flutter settings tree, including the Modules and Apps tabs.
Sideload lists folders in the apps directory, installs one through the store path, and clones an https or ssh URL.
Preferences read and persist through the brain's UserSettingsNeuron (IUserSettings).
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
public sealed record SettingsSavePreferences(
    string? DisplayName = null,
    string? Theme = null,
    string? DefaultModel = null) : DigitalBrain.Signal;

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

public sealed record SettingsAppState(
    string Title,
    string Status,
    string DisplayName,
    string Theme,
    string DefaultModel,
    Node? OverrideBody = null)
{
    public SettingsAppState() : this("Settings", "Settings", "User", "dark", "qwen3.5:4b", null) { }
}

[DigitalBrain.App("settings")]
public sealed class SettingsApp(IPresenter? presenter = null, SideloadDesk? sideload = null) : DigitalBrain.App<SettingsAppState>
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "settings", "Settings", "2.1.0", [typeof(UserSettingsNeuron)], new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = ShellPrograms.Load("Settings");

    [DigitalBrain.On]
    public async Task Open(SettingsOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState("Settings", $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})", user.DisplayName, user.Theme, user.DefaultModel));
    }

    [DigitalBrain.On]
    public async Task Read(SettingsRead request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState("Settings", $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})", user.DisplayName, user.Theme, user.DefaultModel));
    }

    [DigitalBrain.On]
    public void RefreshConnections(SettingsRefreshConnections request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Update(State with { Title = "Settings", Status = "Connections", OverrideBody = null });
    }

    [DigitalBrain.On]
    public async Task SavePreferences(SettingsSavePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DisplayName)
            && string.IsNullOrWhiteSpace(request.Theme)
            && string.IsNullOrWhiteSpace(request.DefaultModel))
        {
            Update(State with { Title = "Settings", Status = "Nothing to save.", OverrideBody = null });
            return;
        }

        var updated = await Get<IUserSettings>("settings").SetProfile(
            request.DisplayName ?? State.DisplayName,
            request.Theme ?? State.Theme,
            request.DefaultModel ?? State.DefaultModel);
        Update(new SettingsAppState("Settings", $"Saved: {updated.DisplayName} ({updated.Theme}, {updated.DefaultModel})", updated.DisplayName, updated.Theme, updated.DefaultModel));
    }

    [DigitalBrain.On]
    public async Task ReloadPreferences(SettingsReloadPreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await Get<IUserSettings>("settings").Read();
        Update(new SettingsAppState("Settings", $"Settings ({user.DisplayName} · {user.Theme} · {user.DefaultModel})", user.DisplayName, user.Theme, user.DefaultModel));
    }

    [DigitalBrain.On]
    public void ReconcilePreferences(SettingsReconcilePreferences request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Update(State with { Title = "Settings", Status = "Settings", OverrideBody = null });
    }

    [DigitalBrain.On]
    public async Task SideloadList(SettingsSideloadList request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Collection([]))
            : await sideload.List(CancellationToken.None);
        Update(State with { Title = view.Title, OverrideBody = view.Root });
    }

    [DigitalBrain.On]
    public async Task SideloadInstall(SettingsSideloadInstall request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Text(string.IsNullOrWhiteSpace(request.Name) ? "Name an app." : request.Name))
            : await sideload.Install(request.Name, CancellationToken.None);
        Update(State with { Title = view.Title, OverrideBody = view.Root });
    }

    [DigitalBrain.On]
    public async Task SideloadAddGit(SettingsSideloadAddGit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = sideload is null
            ? new View("Sideload", new Text(string.IsNullOrWhiteSpace(request.Url) ? "Name a git URL." : request.Url))
            : await sideload.AddGit(request.Url, CancellationToken.None);
        Update(State with { Title = view.Title, OverrideBody = view.Root });
    }

    public View Render() => new(
        State.Title,
        State.OverrideBody ?? new Text(State.Status));

    private void Update(SettingsAppState next)
    {
        Set(next);
        if (presenter is not null)
        {
            Present(presenter.Present, static () => null, Render);
        }
    }

    [DigitalBrain.Scenario("Saving user preferences updates UserSettingsNeuron and view")]
    public async Task VerifySavePreferencesScenario()
    {
        await SavePreferences(new SettingsSavePreferences("Alice", "light", "gpt-5"));
        if (State.DisplayName != "Alice" || State.Theme != "light" || State.DefaultModel != "gpt-5")
        {
            throw new InvalidOperationException($"Expected Alice/light/gpt-5, got {State.DisplayName}/{State.Theme}/{State.DefaultModel}.");
        }
    }
}
