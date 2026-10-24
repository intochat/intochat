using System.Globalization;
using System.Text.Json;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Entries;
using IntoChat.OS.Flutter.Preferences;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Integrations;

// Durable view state ({app}/view-state), so a reconnect shows the same tab. ArchivedTake is the end of the archived page shown.
public sealed record SettingsViewState(string Tab = "general", int ArchivedTake = SettingsView.ArchivedPage, bool ConfirmDelete = false, string? Toast = null);

// What the model reads, so the facts can stand in for the brain.
public sealed record SettingsSources(Func<Task<ModelChoices>> Models, Func<int, int, Task<ChatIndexPage>> Archived, Func<Task<ConnectionRow[]>> Connections);

// Shared by the Settings synapses: keys, the trust rule, view state, the model and the single Show.
public static class Settings
{
    public const string AutoId = "auto";
    public const string NotAvailable = "Not available on this host.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string Scope(IntoChatOSConnection brain)
        => brain.Setting("Brain") is { Length: > 0 } scope ? scope : Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static IText ViewState(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/view-state");
    // Private to the app, never in the tree: the chat ids behind the archived rows last published, by view generation.
    public static IText ArchivedPage(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/archived-page");

    public sealed record PageRecord(long Generation, string[] Ids);

    public static string ArchivedPageJson(long generation, string[] ids) => JsonSerializer.Serialize(new PageRecord(generation, ids), Json);

    public static async Task RecordArchivedPage(IText page, long generation, string[] ids)
    {
        var json = ArchivedPageJson(generation, ids);
        if ((await page.Read()).Markdown != json) { await page.Set(json); }
    }

    public static async Task<PageRecord?> ReadArchivedPage(IText page)
    {
        var stored = (await page.Read()).Markdown;
        if (stored is not { Length: > 0 }) { return null; }
        try { return JsonSerializer.Deserialize<PageRecord>(stored, Json); }
        catch (JsonException) { return null; }
    }
    public static long Revision(IntoChatOSConnection brain) => long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture);

    // Unarchive is a write on the person's behalf: only the Settings the OS ships, unedited, may make it.
    public static bool MayWrite(AppSnapshot self) => ShippedApps.Is(self, "settings");

    // Copy of ChatEndpoints.IndexKey (and Ino.DataKey): Ino's ChatDataApp setting when set, else its own key, plus "/chats".
    public static string ChatIndexKey(string brain, AppSnapshot ino)
        => (ino.Settings.GetValueOrDefault("ChatDataApp") is { Length: > 0 } data ? data : IApp.Key(brain, "ino")) + "/chats";

    // Delete account opens the account page on the host's public origin: https, or http on loopback only, rebuilt
    // from scheme, host and port (no user info). Without one there is nowhere to send the person.
    public static string? DeleteActivation(string? publicOrigin)
        => Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin)
            && (origin.Scheme == Uri.UriSchemeHttps || (origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback))
            ? JsonSerializer.Serialize(new { url = origin.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/account/profile" })
            : null;

    public static async Task ArmDelete(IButton button, string? publicOrigin)
    {
        var current = await button.Read();
        if (DeleteActivation(publicOrigin) is { } activation)
        {
            if (current.Activation != activation || current.Label != SettingsCopy.OpenAccountPage || !current.Enabled)
            { await button.SetActivation(SettingsCopy.OpenAccountPage, activation); }
        }
        else if (current.Enabled || current.Activation is not null || current.Label != SettingsCopy.OpenAccountPage)
        { await button.Set(SettingsCopy.OpenAccountPage, "delete-account", enabled: false); }
    }

    // Desktop.ConnectActivation's shape: the shell's connection window signs in as the person.
    public static string ConnectActivation(string brainId, string connectorId)
        => JsonSerializer.Serialize(new { kind = "connections", brainId, connector = connectorId });

    // The renderer reports a saved key on the key control; any key can change which models are available.
    public static bool IsKeyControl(string appKey, string name) => name.StartsWith(appKey + "/keys/", StringComparison.Ordinal);

    public static async Task<SettingsViewState> ReadState(IText text)
    {
        var stored = (await text.Read()).Markdown;
        SettingsViewState? state = null;
        if (stored is { Length: > 0 })
        {
            try { state = JsonSerializer.Deserialize<SettingsViewState>(stored, Json); }
            catch (JsonException) { }
        }
        state ??= new();
        var take = Math.Max(SettingsView.ArchivedPage, state.ArchivedTake / SettingsView.ArchivedPage * SettingsView.ArchivedPage);
        return state with { Tab = SettingsTabs.Id(state.Tab), ArchivedTake = take };
    }

    // Version-checked so two clicks landing together both stick.
    public static async Task UpdateState(IText text, Func<SettingsViewState, SettingsViewState> change)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var version = (await text.Read()).Version;
            var next = change(await ReadState(text));
            if (await text.TrySet(JsonSerializer.Serialize(next, Json), version)) { return; }
        }
        throw new InvalidOperationException("Settings changed too often to save the view.");
    }

    public static int ArchivedSkip(SettingsViewState state) => state.ArchivedTake - SettingsView.ArchivedPage;

    // Each tab loads only what it shows. A source that fails leaves its list empty and says so.
    public static async Task<SettingsModel> Model(string appKey, SettingsViewState state, SettingsSources sources)
    {
        var toast = state.Toast;
        ModelRow auto = new(AutoId, SettingsCopy.AutoRow, true);
        ModelRow[] models = [];
        ArchivedRow[] archived = [];
        var archivedTotal = 0;
        ConnectionRow[] connections = [];
        try
        {
            if (state.Tab == "models")
            {
                var choices = await sources.Models();
                auto = new(AutoId, SettingsCopy.AutoRow, choices.Automatic.Available);
                models = [.. choices.Models.Where(model => model.Id is { Length: > 0 }).Select(model => new ModelRow(model.Id!, model.Label, model.Available))];
            }
            else if (state.Tab == "archived")
            {
                var skip = ArchivedSkip(state);
                var page = await sources.Archived(skip, SettingsView.ArchivedPage);
                archived = [.. page.Entries.Select(entry => new ArchivedRow(entry.Title, entry.UpdatedAt))];
                // The view shows Show more while more remain after this page.
                archivedTotal = Math.Max(0, page.Total - skip);
            }
            else if (state.Tab == "connections") { connections = await sources.Connections(); }
        }
        catch (Exception error) when (error is not OperationCanceledException) { toast = SettingsCopy.SaveFailed; }
        return new(state.Tab, appKey, auto, models, archived, archivedTotal, connections, state.ConfirmDelete && state.Tab == "danger", toast);
    }

    // The brain's connections first, then each connector this host offers that is not connected yet.
    public static ConnectionRow[] Connections(ConnectionSummary[] current, ConnectorKind[] kinds, ConnectorOffer[] offers)
    {
        string Provider(string id) => kinds.FirstOrDefault(kind => kind.Id == id)?.DisplayName
            ?? offers.FirstOrDefault(offer => offer.Id == id)?.DisplayName ?? id;
        var rows = current.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new ConnectionRow(item.Kind, Provider(item.Kind) + " · " + (item.Account is { Length: > 0 } account ? account : item.Id),
                State(item.Status), item.Status == "connected")).ToList();
        foreach (var offer in offers.Where(offer => current.All(item => item.Kind != offer.Id)))
        { rows.Add(new(offer.Id, offer.DisplayName, offer.Available ? State("disconnected") : NotAvailable, false, offer.Available)); }
        return [.. rows];
    }

    private static string State(string value) => value switch
    {
        "connected" => "Connected",
        "expired" => "Authorization expired",
        "failing" => "Unavailable, check the connection",
        "disconnected" => "Not connected",
        _ => "Configuration required",
    };

    public static SettingsSources Sources(IntoChatOSConnection brain)
    {
        var scope = Scope(brain);
        var requests = brain.Get<IConnectionRequests>(scope);
        return new(
            () => brain.Get<IModelCatalog>(IModelCatalog.Key(scope)).List(),
            async (skip, take) => await (await Index(brain)).List(true, skip, take),
            async () => Connections(await requests.ListConnections(), await requests.ListKinds(), await requests.ListOffers()));
    }

    public static async Task<IChatIndex> Index(IntoChatOSConnection brain)
        => brain.Get<IChatIndex>(ChatIndexKey(Scope(brain), await brain.Get<IApp>(IApp.Key(Scope(brain), "ino")).Read()));

    public static SettingsCommands Commands(IntoChatOSConnection brain)
        => new(ViewState(brain), ArchivedPage(brain), () => brain.Get<IApp>(Key(brain)).Read(), () => Index(brain));

    // The preference controls the view references, by name under {app}/pref/.
    public static IEnumerable<(string Name, PreferenceSpec Spec)> Preferences()
    {
        yield return ("display-name", new(PreferenceStore.LocalDisplayName, SettingsCopy.DisplayName, PreferenceKind.Text));
        yield return ("theme", new(PreferenceStore.LocalTheme, SettingsCopy.Theme, PreferenceKind.Choice,
            [new("intochat", "IntoChat"), new("midnight", "Midnight"), new("paper", "Paper"), new("lumen", "Lumen")]));
        yield return ("compact", new(PreferenceStore.LocalCompactSpacing, SettingsCopy.CompactSpacing, PreferenceKind.Toggle));
        yield return ("reduced-motion", new(PreferenceStore.LocalReducedMotion, SettingsCopy.ReducedMotion, PreferenceKind.Toggle));
        yield return ("new-tab", new(PreferenceStore.NewTabApp, SettingsCopy.NewTab, PreferenceKind.Choice));
        PreferenceStore[] stores = [PreferenceStore.NotifyAgentFinished, PreferenceStore.NotifyNeedsApproval, PreferenceStore.NotifyConnectionErrors, PreferenceStore.NotifyProductUpdates];
        for (var i = 0; i < SettingsCopy.Notifications.Length; i++)
        { yield return ("notify-" + SettingsCopy.Notifications[i].Key, new(stores[i], SettingsCopy.Notifications[i].Label, PreferenceKind.Toggle)); }
    }

    // Configure writes only on a change, so reopening Settings does not churn every control.
    public static async Task Configure(IPreferenceControl control, PreferenceSpec spec)
    {
        var current = (await control.Read()).Spec;
        // The new tab neuron fills its choices on read; the spec itself holds none.
        static string Shape(PreferenceSpec? value) => JsonSerializer.Serialize(value?.Store == PreferenceStore.NewTabApp ? value with { Choices = null } : value);
        if (Shape(current) != Shape(spec)) { await control.Configure(spec); }
    }

    public static async Task Configure(IEntryControl control, EntryControlSpec spec)
    {
        if ((await control.Read()).Spec != spec) { await control.Configure(spec); }
    }

    // Every control the view references, then the buttons, before the first publish.
    public static async Task ConfigureControls(IntoChatOSConnection brain)
    {
        var app = Key(brain);
        foreach (var (name, spec) in Preferences()) { await Configure(brain.Get<IPreferenceControl>(app + "/pref/" + name), spec); }
        foreach (var (id, label) in SettingsCopy.KeyProviders)
        { await Configure(brain.Get<IEntryControl>(app + "/keys/" + id), new(EntryControlMode.Key, label, "provider:" + id)); }
        await Configure(brain.Get<IEntryControl>(app + "/variables"), new(EntryControlMode.Collection, SettingsCopy.Variables));
        await ArmDelete(brain.Get<IButton>(app + "/delete-account"), brain.Setting("PublicOrigin"));
    }

    public static async Task Show(IntoChatOSConnection brain)
    {
        // A concurrent publish conflicts: retry once against the fresh version.
        try { await Render(brain); }
        catch (InvalidOperationException) { await Render(brain); }
    }

    private static async Task Render(IntoChatOSConnection brain)
    {
        var app = Key(brain);
        var state = await ReadState(ViewState(brain));
        var sources = Sources(brain);
        ChatIndexPage? shown = null;
        var model = await Model(app, state, sources with { Archived = async (skip, take) => shown = await sources.Archived(skip, take) });
        // Recorded before the publish, so a click on these rows resolves to these ids.
        if (shown is not null)
        { await RecordArchivedPage(ArchivedPage(brain), Revision(brain), [.. shown.Entries.Take(SettingsView.ArchivedPage).Select(entry => entry.Id)]); }
        if (state.Tab == "models")
        {
            await Configure(brain.Get<IPreferenceControl>(app + "/pref/default-model"),
                new(PreferenceStore.LocalDefaultModel, SettingsCopy.DefaultModel, PreferenceKind.Choice, SettingsView.ModelChoices(model)));
        }
        foreach (var row in model.Connections.Take(SettingsView.ConnectionsShown).Where(row => row is { Connected: false, Connectable: true }).DistinctBy(row => row.Connector))
        {
            var button = brain.Get<IButton>(app + "/connect/" + row.Connector);
            var activation = ConnectActivation(Scope(brain), row.Connector);
            var current = await button.Read();
            if (current.Activation != activation || current.Label != SettingsCopy.Connect) { await button.SetActivation(SettingsCopy.Connect, activation); }
        }
        await new ViewPublisher(brain.Get<IView>(app + "/view"), Revision(brain)).Publish(SettingsView.Render(model));
    }

    public static Task Present(IntoChatOSConnection brain)
        => brain.Get<IApp>(Key(brain)).Present(Revision(brain), new(new Sphere("icon.svg", []),
            Main: new(UIVocabulary.ViewType, Key(brain) + "/view"), Views: [new("full", new(UIVocabulary.ViewType, Key(brain) + "/view"))]));
}

// The view's own actions as state changes. Each returns null for an action it does not own (copy: and older
// clients' actions among them), so a handler skips it.
public sealed class SettingsCommands(IText viewState, IText archivedPage, Func<Task<AppSnapshot>> self, Func<Task<IChatIndex>> index)
{
    public static Task Open(IText viewState, string? tab)
        => Settings.UpdateState(viewState, state => state with { Tab = SettingsTabs.Id(tab), ConfirmDelete = false, Toast = null });

    // generation is the clicked view's (ViewAction.Generation); unarchive resolves its row only against that render.
    public Func<Task>? Command(string action, long generation = 0)
    {
        var at = action.IndexOf(':', StringComparison.Ordinal);
        var (verb, argument) = at < 0 ? (action, "") : (action[..at], action[(at + 1)..]);
        return verb switch
        {
            "tab" when SettingsTabs.All.Any(tab => tab.Id == argument)
                => () => Update(state => state with { Tab = argument, ConfirmDelete = false, Toast = null }),
            "archived-more" when at < 0 => () => Update(state => state with { ArchivedTake = state.ArchivedTake + SettingsView.ArchivedPage, Toast = null }),
            "delete" when at < 0 => () => Update(state => state with { ConfirmDelete = true, Toast = null }),
            "delete-cancel" when at < 0 => () => Update(state => state with { ConfirmDelete = false }),
            "unarchive" when int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var row) && row < SettingsView.ArchivedPage
                => () => Unarchive(row, generation),
            _ => null,
        };
    }

    private Task Update(Func<SettingsViewState, SettingsViewState> change) => Settings.UpdateState(viewState, change);

    // The row index names the chat the person saw: the id recorded for that render. Another render's click, or an
    // index past the page, is refused with a toast. Unarchiving an already active chat is harmless.
    private async Task Unarchive(int row, long generation)
    {
        if (!Settings.MayWrite(await self())) { return; }
        var chats = await index();
        if (await Settings.ReadArchivedPage(archivedPage) is not { } page || page.Generation != generation || row >= page.Ids.Length
            || await chats.Get(page.Ids[row]) is null)
        {
            await Update(current => current with { Toast = SettingsCopy.ListChanged });
            return;
        }
        await chats.Unarchive(page.Ids[row]);
        await Update(current => current with { Toast = SettingsCopy.ChatRestored });
    }
}
