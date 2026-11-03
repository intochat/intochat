using DigitalBrain.Capabilities.Connections.Requests;
using System.Globalization;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Button.Signals;
using IntoChat.OS.Modules.Flutter.Entries;
using IntoChat.OS.Modules.Flutter.Entries.Signals;
using IntoChat.OS.Modules.Flutter.Select;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Text.Signals;
using IntoChat.OS.Modules.Flutter.Views;

[DigitalBrain.App("settings")]
public sealed class SettingsProgram : DigitalBrain.App
{
    // Configures every control the view references, arms its buttons and presents the view on the durable tab.
    // `open` takes a tab id (or an old section name); `read` publishes the stored preferences.
    [DigitalBrain.On("open")]
    public Task Open(string? input) => Act(SettingsCopy.OpenFailed, async brain =>
    {
        await SettingsCommands.Open(Settings.ViewState(brain), input);
        await Compose(brain);
        await new SettingsPreferences(brain).Publish();
    });

    [DigitalBrain.On("read")]
    public Task Read(string? input) => Act(SettingsCopy.OpenFailed, brain => new SettingsPreferences(brain).Publish());

    // Shows the brain's connections and the host's connector offers again, arming each Connect button.
    [DigitalBrain.On("refresh-connections")]
    public Task RefreshConnections(string? input) => Act(null, Settings.Show);

    [DigitalBrain.On("save-preferences")]
    public Task SavePreferences(string? input) => Act(null, brain => new SettingsPreferences(brain).Run("save-preferences", input ?? ""));

    [DigitalBrain.On("reload-preferences")]
    public Task ReloadPreferences(string? input) => Act(null, brain => new SettingsPreferences(brain).Run("reload-preferences", ""));

    [DigitalBrain.On("reconcile-preferences")]
    public Task ReconcilePreferences(string? input) => Act(null, brain => new SettingsPreferences(brain).Run("reconcile-preferences", ""));

    protected override void Watches(DigitalBrain.WatchBuilder watch)
    {
        // The Settings view's own navigation: `tab:<id>`, `archived-more`, `delete`, `delete-cancel` and `unarchive:<token>`.
        // Unarchive writes only from the unedited shipped Settings (SettingsCommands checks Settings.MayWrite).
        watch.On<IView, ViewAction>("view", at => at.App + "/view", clicked => Act(
            clicked.Action.StartsWith("unarchive:", StringComparison.Ordinal) ? SettingsCopy.SaveFailed : null, async brain =>
            {
                if (Settings.Commands(brain).Command(clicked.Action) is not { } command) { return; }
                await command();
                await Settings.Show(brain);
            }));
        // The Apps tab's navigation: `detail:<section>/<i>`, `back`, `remove-ask`, `remove-cancel`.
        watch.On<IView, ViewAction>("apps-view", at => at.App + "/apps-view", clicked => Act(null, async brain =>
        {
            if (Apps.Command(brain, clicked.Action) is not { } command) { return; }
            await command();
            await Apps.Show(brain);
        }));
        // The app the Apps tab shows was installed, updated or removed: show it again, so its status and actions follow.
        watch.OnEach<IText, TextChanged, IApp, AppChanged>("shown-app", at => at.App + "/apps-view-state", async (state, at, _) =>
        {
            var target = AppsNames.Parse((await Apps.ReadState(state)).Target);
            return (target.Installed ?? target.App) is { } name ? [IApp.Key(at.Brain, name)] : [];
        }, _ => Act(null, Apps.Show));
        // A slot's choice changed: show the detail again, which re-arms Install with the chosen connections.
        for (var i = 0; i < AppsView.SlotsShown; i++)
        {
            var slot = "/slot/" + i.ToString(CultureInfo.InvariantCulture);
            watch.On<ISelect, SelectChanged>("slot-" + i.ToString(CultureInfo.InvariantCulture), at => at.App + slot, _ => Act(null, Apps.Show));
        }
        // A connection was added, changed, removed or requested: both tabs list connections.
        watch.On<IConnectionRequests, ConnectionsChanged>("connections", at => IConnectionRequests.Key(at.Brain), _ => Act(null, async brain =>
        {
            await Settings.Show(brain);
            await Apps.Show(brain);
        }));
        watch.On<IConnectionRequests, ConnectionRequested>("connection-requested", at => IConnectionRequests.Key(at.Brain), _ => Act(null, Settings.Show));
        // A key saved or removed can change which models are available. The signal carries the control's name only, never a value.
        foreach (var (id, _) in SettingsCopy.KeyProviders)
        {
            watch.On<IEntryControl, EntryControlChanged>("key-" + id, at => at.App + "/keys/" + id, _ => Act(null, Settings.Show));
        }
        // The preference buttons run the operation their action names; Save carries the draft and baseline.
        watch.On<IButton, ButtonClicked>("save-preferences", at => at.App + "/save-preferences", _ => Act(null, async brain =>
        {
            var preferences = new SettingsPreferences(brain);
            await preferences.Run("save-preferences", await preferences.Submission());
        }));
        watch.On<IButton, ButtonClicked>("reload-preferences", at => at.App + "/reload-preferences",
            _ => Act(null, brain => new SettingsPreferences(brain).Run("reload-preferences", "")));
    }

    // Present even if a control or the first render fails: the next action renders again.
    private static async Task Compose(IntoChatOSConnection brain)
    {
        try
        {
            await Settings.ConfigureControls(brain);
            await Settings.Show(brain);
        }
        finally { await Settings.Present(brain); }
    }

    // A failed action is logged by type only and, where it has copy, toasted; it never poisons the subscription.
    // Generic copy only, never the error text.
    private static async Task Act(string? toast, Func<IntoChatOSConnection, Task> action)
    {
        await using var brain = await IntoChatOSClient.ConnectAsync([]);
        try { await action(brain); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine("Settings could not complete an action: " + error.GetType().Name);
            if (toast is null) { return; }
            try
            {
                await Settings.UpdateState(Settings.ViewState(brain), state => state with { Toast = toast });
                await Settings.Show(brain);
            }
            catch (Exception) { }
        }
    }
}
