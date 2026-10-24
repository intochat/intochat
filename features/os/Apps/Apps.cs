using System.Globalization;
using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Select;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Integrations;

// Durable view state ({app}/view-state): what is shown (AppsTarget's text, "" for the library), whether Remove is
// being confirmed, and a toast.
public sealed record AppsViewState(string Target = "", bool ConfirmRemove = false, string? Toast = null);

// Shared by the Apps synapses: keys, view state, the model, arming the button neurons and the single Show.
public static class Apps
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string Scope(IntoChatOSConnection brain)
        => brain.Setting("Brain") is { Length: > 0 } scope ? scope : Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];
    public static IText ViewState(IntoChatOSConnection brain) => brain.Get<IText>(Key(brain) + "/view-state");
    public static IAppStore Store(IntoChatOSConnection brain) => brain.Get<IAppStore>(IAppStore.Key(Scope(brain)));
    public static IConnectionRequests Connections(IntoChatOSConnection brain) => brain.Get<IConnectionRequests>(Scope(brain));
    public static ISelect Slot(IntoChatOSConnection brain, int i) => brain.Get<ISelect>(Key(brain) + "/slot/" + i.ToString(CultureInfo.InvariantCulture));
    public static long Revision(IntoChatOSConnection brain) => long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture);

    public static async Task<AppsViewState> ReadState(IText text)
    {
        var stored = (await text.Read()).Markdown;
        AppsViewState? state = null;
        if (stored is { Length: > 0 })
        {
            try { state = JsonSerializer.Deserialize<AppsViewState>(stored, Json); }
            catch (JsonException) { }
        }
        state ??= new();
        return state with { Target = AppsNames.Parse(state.Target).ToString() };
    }

    // Version-checked so two clicks landing together both stick.
    public static async Task UpdateState(IText text, Func<AppsViewState, AppsViewState> change)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var version = (await text.Read()).Version;
            var next = change(await ReadState(text));
            if (await text.TrySet(JsonSerializer.Serialize(next, Json), version)) { return; }
        }
        throw new InvalidOperationException("Apps changed too often to save the view.");
    }

    // The library: the gallery's apps, each described for its version and status, and its connections.
    public static async Task<(AppDetail[] Installed, AppDetail[] Available, GalleryEntry[] Connections)> Library(IAppStore store)
    {
        var entries = await store.List();
        List<AppDetail> installed = [], available = [];
        foreach (var entry in entries.Where(entry => entry.Kind == "app"))
        {
            var detail = entry.Publisher.Length > 0 ? await Describe(store, new(entry.Publisher, entry.Name)) : null;
            detail ??= new(entry.Publisher, entry.Name, entry.Title, entry.Description, entry.Icon, null, entry.InstalledName, null, false,
                new(), null, [], entry.Installed ? (entry.UpdateAvailable ? "update" : "installed") : "available", null);
            // The gallery's title wins: an edited copy reads "Copy of ...".
            detail = detail with { Title = entry.Title };
            (entry.Installed ? installed : available).Add(detail);
        }
        return ([.. installed.Take(AppsView.RowsShown)], [.. available.Take(AppsView.RowsShown)], [.. entries.Where(entry => entry.Kind == "connection")]);
    }

    private static async Task<AppDetail?> Describe(IAppStore store, AppRef reference)
    {
        try { return await store.Describe(reference); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    // The target a library row's Details names, resolved against the library as it is now.
    public static async Task<string?> RowTarget(IAppStore store, string argument)
    {
        var slash = argument.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || !int.TryParse(argument[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var i)) { return null; }
        var (installed, available, _) = await Library(store);
        var rows = argument[..slash] switch { "installed" => installed, "available" => available, _ => null };
        if (rows is null || i >= rows.Length) { return null; }
        var row = rows[i];
        return row.InstalledName is { Length: > 0 } name ? "installed:" + name
            : row.Publisher.Length > 0 ? "app:" + row.Publisher + "/" + row.App : null;
    }

    // One app's detail. A pinned release that is neither the latest listing nor the installed copy is unavailable.
    public static async Task<AppDetail?> Detail(IntoChatOSConnection brain, AppsTarget target)
    {
        var store = Store(brain);
        if (target.Installed is { } name)
        {
            var app = await brain.Get<IApp>(IApp.Key(Scope(brain), name)).Read();
            if (app.Manifest.Title.Length == 0) { return null; }
            var listed = app.Origin is { } origin ? await Describe(store, new(origin.Publisher, origin.Name)) : null;
            return AppsView.ForInstalled(name, app, listed);
        }
        if (target.App is null) { return null; }
        var detail = await Describe(store, new(target.Publisher!, target.App));
        if (detail is null) { return null; }
        if (target.Version is { } pinned && pinned != detail.LatestVersion && pinned != detail.InstalledVersion)
        { return detail with { Status = "unavailable", Reason = AppsCopy.ReleaseGone, InstalledName = detail.InstalledName }; }
        return detail;
    }

    public static async Task<AppsModel> Model(IntoChatOSConnection brain, AppsViewState state)
    {
        var target = AppsNames.Parse(state.Target);
        if (!target.IsLibrary)
        {
            try
            {
                if (await Detail(brain, target) is { } detail)
                {
                    var share = AppsNames.ShareLink(brain.Setting("PublicOrigin"), detail.Publisher, detail.App, target.Version);
                    return new([], [], [], detail, share, state.ConfirmRemove, state.Toast, null);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Console.Error.WriteLine("Apps could not describe an app: " + error.GetType().Name); }
        }
        try
        {
            var (installed, available, connections) = await Library(Store(brain));
            return new(installed, available, connections, null, null, false, state.Toast, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine("Apps could not load the library: " + error.GetType().Name);
            return new([], [], [], null, null, false, state.Toast, AppsCopy.LoadFailed);
        }
    }

    private static async Task Arm(IButton button, string label, string activation)
    {
        var current = await button.Read();
        if (current.Activation != activation || current.Label != label || !current.Enabled) { await button.SetActivation(label, activation); }
    }

    // Each slot offers this brain's connected connections of its kind, then "Add a new ... connection". A choice that
    // is gone falls back to the slot's own new connection when it now exists, else the first, else new.
    private static async Task<string?[]> ArmSlots(IntoChatOSConnection brain, AppDetail detail)
    {
        var slots = AppsView.Slots(detail);
        if (slots.Length == 0) { return []; }
        var requests = Connections(brain);
        var connected = (await requests.ListConnections()).Where(item => item.Status == "connected").ToArray();
        var kinds = await requests.ListKinds();
        var choices = new string?[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            var provider = kinds.FirstOrDefault(kind => kind.Id == slot.Connector)?.DisplayName ?? slot.Connector;
            SelectOption[] options =
            [
                .. connected.Where(item => item.Kind == slot.Connector).OrderBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => new SelectOption(item.Id, provider + " · " + (item.Account is { Length: > 0 } account ? account : item.Id))),
                new(AppsView.NewChoice, AppsCopy.NewConnection(provider)),
            ];
            var select = Slot(brain, i);
            var current = await select.Read();
            var own = AppsView.SlotConnection(detail, slot);
            var chosen = AppsView.Choose(current.Selected, [.. options.Where(option => option.Id != AppsView.NewChoice).Select(option => option.Id)], own);
            if (current.Label != slot.Name || current.Selected != chosen || !current.Options.SequenceEqual(options))
            { await select.Set(slot.Name, options, chosen); }
            choices[i] = chosen;
            await Arm(brain.Get<IButton>(Key(brain) + "/slot-new/" + i.ToString(CultureInfo.InvariantCulture)), AppsCopy.AddNew,
                AppsView.ConnectActivation(Scope(brain), slot.Connector, own));
        }
        return choices;
    }

    // Every button the tree references carries its activation before the publish. Install, update and remove are
    // set by this app through its own proxy for its own brain (the button neuron accepts them from nothing else).
    private static async Task Disarm(IButton button, string label, string action)
    {
        var current = await button.Read();
        if (current.Activation is not null || current.Enabled || current.Label != label) { await button.Set(label, action, enabled: false); }
    }

    private static async Task<AppsModel> ArmButtons(IntoChatOSConnection brain, AppsModel model)
    {
        var app = Key(brain);
        var scope = Scope(brain);
        if (model.Detail is not { } detail)
        {
            foreach (var entry in model.Connections.Where(entry => entry is { Available: true, Installed: false, ConnectorId: { Length: > 0 } }).Take(AppsView.ConnectionsShown))
            { await Arm(brain.Get<IButton>(AppsView.ConnectButton(app, entry.ConnectorId!)), AppsCopy.Connect, AppsView.ConnectActivation(scope, entry.ConnectorId!)); }
            return model;
        }
        var choices = await ArmSlots(brain, detail);
        if (AppsView.Choosing(detail))
        {
            var install = brain.Get<IButton>(app + "/install");
            if (AppsView.InstallArming(scope, detail, choices, Guid.NewGuid()) is { } activation) { await Arm(install, AppsCopy.Install, activation); }
            else
            {
                await Disarm(install, AppsCopy.Install, "install");
                model = model with { SlotsUnbound = true };
            }
        }
        if (detail.InstalledName is { Length: > 0 } name)
        {
            await Arm(brain.Get<IButton>(app + "/open"), AppsCopy.Open, AppsView.OpenActivation(scope, name));
            if (detail.Status == "update") { await Arm(brain.Get<IButton>(app + "/update"), AppsCopy.Update, AppsView.UpdateActivation(scope, detail)); }
            var remove = brain.Get<IButton>(app + "/remove");
            if (AppsView.RemoveArming(scope, model) is { } activation) { await Arm(remove, AppsCopy.Remove, activation); }
            else { await Disarm(remove, AppsCopy.Remove, "remove"); }
        }
        if ((AppsView.Asks(detail).Shown.Secrets ?? []).Length > 0)
        { await Arm(brain.Get<IButton>(app + "/to-variables"), AppsCopy.ManageSecrets, AppsView.VariablesActivation(scope)); }
        return model;
    }

    public static async Task Show(IntoChatOSConnection brain)
    {
        // A concurrent publish conflicts: retry once against the fresh version.
        try { await Render(brain); }
        catch (InvalidOperationException) { await Render(brain); }
    }

    private static async Task Render(IntoChatOSConnection brain)
    {
        var model = await Model(brain, await ReadState(ViewState(brain)));
        model = await ArmButtons(brain, model);
        await new ViewPublisher(brain.Get<IView>(Key(brain) + "/view"), Revision(brain)).Publish(AppsView.Render(Key(brain), model));
    }

    public static Task Present(IntoChatOSConnection brain)
        => brain.Get<IApp>(Key(brain)).Present(Revision(brain), new(new Sphere("icon.svg", []),
            Main: new(UIVocabulary.ViewType, Key(brain) + "/view"), Views: [new("full", new(UIVocabulary.ViewType, Key(brain) + "/view"))]));

    // The view's own navigation as state changes; null for an action this app does not own (copy: among them).
    public static Func<Task>? Command(IntoChatOSConnection brain, string action)
    {
        var at = action.IndexOf(':', StringComparison.Ordinal);
        var (verb, argument) = at < 0 ? (action, "") : (action[..at], action[(at + 1)..]);
        var state = ViewState(brain);
        return verb switch
        {
            "detail" when argument.Length is > 0 and <= 32 => async () =>
            {
                if (await RowTarget(Store(brain), argument) is { } target)
                { await UpdateState(state, current => current with { Target = target, ConfirmRemove = false, Toast = null }); }
            },
            "back" when at < 0 => () => UpdateState(state, current => current with { Target = "", ConfirmRemove = false, Toast = null }),
            "remove-ask" when at < 0 => () => UpdateState(state, current => current with { ConfirmRemove = true, Toast = null }),
            "remove-cancel" when at < 0 => () => UpdateState(state, current => current with { ConfirmRemove = false }),
            _ => null,
        };
    }

    public static Task Open(IText viewState, string? input)
        => UpdateState(viewState, state => state with { Target = AppsNames.Parse(input).ToString(), ConfirmRemove = false, Toast = null });
}
