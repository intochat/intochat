using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;

// Every string the Apps view shows, verbatim from the R5 plan where it gives one.
public static class AppsCopy
{
    public const string Title = "Apps";
    public const string Details = "Details";
    public const string Back = "Back";
    public const string Installed = "Installed";
    public const string UpdateAvailable = "Update available";
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";
    public const string AsksFor = "This app asks for";
    public const string AsksForMore = "This update asks for more:";
    public const string ReleaseGone = "This release is no longer available.";
    public const string SharingOff = "Sharing isn't set up on this host.";
    public const string CopyLink = "Copy link";
    public const string Install = "Install";
    public const string Update = "Update";
    public const string Open = "Open";
    public const string Remove = "Remove";
    public const string Cancel = "Cancel";
    public const string RemoveDetail = "Its data stays in this brain.";
    public const string Data = "Data";
    public const string Connections = "Connections";
    public const string Secrets = "Secrets";
    public const string ManageSecrets = "Variables and secrets";
    public const string Connect = "Connect";
    public const string NotAvailable = "Not available";
    public const string AddNew = "Add new";
    public const string LoadFailed = "The apps could not be loaded.";
    public static string MadeBy(string publisher) => "Made by " + (publisher == ShippedApps.Publisher ? AppsNames.ShippedDisplay : publisher);
    public static string RemoveTitle(string title) => "Remove " + title + "?";
    public static string NewConnection(string connector) => "Add a new " + connector + " connection";
}

public sealed record AppsModel(IReadOnlyList<AppDetail> Installed, IReadOnlyList<AppDetail> Available, IReadOnlyList<GalleryEntry> Connections,
    AppDetail? Detail, string? Share, bool ConfirmRemove, string? Toast, string? Notice);

// Pure composition of the Apps surface: the library (installed, available, connections) or one app's detail with
// what it asks for. Install, Update, Remove, Open, slot choices and Add new are neurons the view references, armed by
// Apps.cs with the activations built here, so the tree itself never carries a request.
public static class AppsView
{
    // Rows a list shows at most, so the tree stays inside IView's 512 nodes.
    public const int RowsShown = 32;
    public const int ConnectionsShown = 32;
    public const int SlotsShown = 8;
    public const string NewChoice = "new";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static UiNode Render(string appKey, AppsModel m) => m.Detail is { } detail ? Detail(appKey, m, detail) : Library(appKey, m);

    public static (string Label, UiTone Tone) Status(string status) => status switch
    {
        "installed" => (AppsCopy.Installed, UiTone.Done),
        "update" => (AppsCopy.UpdateAvailable, UiTone.Working),
        "unavailable" => (AppsCopy.Unavailable, UiTone.Error),
        _ => (AppsCopy.Available, UiTone.Neutral),
    };

    public static UiNode Library(string appKey, AppsModel m) => Ui.Column("apps",
    [
        Ui.Text("apps-title", AppsCopy.Title),
        .. m.Notice is { Length: > 0 } notice ? [Ui.Text("notice", notice)] : Array.Empty<UiNode>(),
        Ui.List("installed", Rows("installed", m.Installed)),
        Ui.List("available", Rows("available", m.Available)),
        Ui.Column("connections", [.. m.Connections.Where(entry => entry.ConnectorId is { Length: > 0 }).Take(ConnectionsShown).Select(entry => Connection(appKey, entry))], gap: 4),
        .. Toast(m),
    ]);

    private static UiNode[] Rows(string section, IReadOnlyList<AppDetail> apps) =>
    [
        .. apps.Take(RowsShown).Select((app, i) =>
        {
            var at = section + "-" + i.ToString(CultureInfo.InvariantCulture);
            var (label, tone) = Status(app.Status);
            return Ui.Row("app-" + at,
            [
                Ui.Text("title-" + at, app.Title),
                Ui.Text("display-" + at, AppsNames.Display(app.Publisher, app.App)),
                Ui.Text("version-" + at, "v" + (app.InstalledVersion ?? app.LatestVersion ?? "")),
                Ui.Badge("status-" + at, label, tone),
                Ui.Button("details-" + at, AppsCopy.Details, "detail:" + section + "/" + i.ToString(CultureInfo.InvariantCulture)),
            ], extents: [0, 160, 72, 96, 88]);
        }),
    ];

    // The gallery's keys, so a connection card reads the same wherever it shows.
    public static string ConnectButton(string appKey, string connector) => appKey + "/connect/" + connector;

    private static UiNode Connection(string appKey, GalleryEntry entry)
    {
        var id = "connection/" + entry.ConnectorId;
        UiNode[] action = entry.Installed ? []
            : !entry.Available ? [Ui.Button("connect-" + id, AppsCopy.NotAvailable, "connect:" + entry.ConnectorId, enabled: false)]
            : [Ui.Reference("connect-" + id, new(UIVocabulary.ButtonType, ConnectButton(appKey, entry.ConnectorId!)))];
        return Ui.Card("entry-" + id, entry.Title, entry.Description, action);
    }

    public static UiNode Detail(string appKey, AppsModel m, AppDetail d)
    {
        var installed = d.InstalledName is { Length: > 0 };
        List<UiNode> nodes =
        [
            Ui.Row("detail-header",
            [
                Ui.IconButton("back", "arrow-left", AppsCopy.Back, "back"),
                Ui.Text("detail-title", d.Title),
                Ui.Text("detail-display", AppsNames.Display(d.Publisher, d.App)),
            ]),
            Ui.Card("about", d.Description, AppsCopy.MadeBy(d.Publisher)),
        ];
        if (Versions(d) is { Length: > 0 } versions) { nodes.Add(Ui.Text("versions", versions)); }
        if (d.Status == "unavailable") { nodes.Add(Ui.Badge("unavailable", AppsCopy.ReleaseGone, UiTone.Error)); }
        if (Requirements(appKey, d) is { } requires) { nodes.Add(requires); }
        if (Actions(appKey, d, installed) is { Length: > 0 } actions) { nodes.Add(Ui.Row("actions", actions)); }
        nodes.Add(m.Share is { Length: > 0 } link ? Ui.CopyButton("share", AppsCopy.CopyLink, link) : Ui.Text("share-off", AppsCopy.SharingOff));
        if (m.ConfirmRemove && MayRemove(d))
        {
            nodes.Add(Ui.Modal("confirm-remove", AppsCopy.RemoveTitle(d.Title), AppsCopy.RemoveDetail,
            [
                Ui.Reference("remove-go", new(UIVocabulary.ButtonType, appKey + "/remove")),
                Ui.Button("remove-cancel", AppsCopy.Cancel, "remove-cancel", style: UiStyle.Ghost),
            ]));
        }
        nodes.AddRange(Toast(m));
        return Ui.Column("app-detail", [.. nodes]);
    }

    // The OS's roots stay installed (the store refuses them), so they offer no Remove.
    public static bool MayRemove(AppDetail d) => d.InstalledName is { Length: > 0 } name && !ShippedApps.Roots.Contains(name);

    public static string Versions(AppDetail d) => string.Join(" · ", new[]
    {
        d.InstalledVersion is { Length: > 0 } installed ? "Installed " + installed : null,
        d.LatestVersion is { Length: > 0 } latest ? "Latest " + latest : null,
    }.OfType<string>());

    // An update that adds requirements shows only what it adds, under its own heading; anything else shows all.
    public static (string Heading, AppRequirements Shown) Asks(AppDetail d)
    {
        var added = AppConsent.Added(d.Consented, d.Requires);
        return d.Status == "update" && !AppConsent.IsEmpty(added) ? (AppsCopy.AsksForMore, added) : (AppsCopy.AsksFor, d.Requires);
    }

    // Slot choices exist only before install: an installed app's connections are bound already.
    public static bool Choosing(AppDetail d) => d.Status == "available" && d.InstalledName is null;

    public static AppSlot[] Slots(AppDetail d) => Choosing(d) ? [.. d.Slots.Take(SlotsShown)] : [];

    private static UiNode? Requirements(string appKey, AppDetail d)
    {
        var (heading, shown) = Asks(d);
        var slots = Slots(d);
        var connectors = (shown.Connectors ?? []).Where(connector => slots.All(slot => slot.Connector != connector)).ToArray();
        if (AppConsent.IsEmpty(shown) && slots.Length == 0) { return null; }
        List<UiNode> lists = [];
        if ((shown.SemanticTypes ?? []).Length > 0)
        {
            lists.Add(Ui.Text("requires-data-heading", AppsCopy.Data));
            lists.Add(Ui.List("requires-data", [.. shown.SemanticTypes!.Select((type, i) => Ui.Text("data-" + i.ToString(CultureInfo.InvariantCulture), type))]));
        }
        if (slots.Length + connectors.Length > 0)
        {
            lists.Add(Ui.Text("requires-connections-heading", AppsCopy.Connections));
            lists.Add(Ui.List("requires-connections",
            [
                .. slots.Select((slot, i) =>
                {
                    var at = i.ToString(CultureInfo.InvariantCulture);
                    return Ui.Row("slot-row-" + at,
                    [
                        Ui.Text("slot-name-" + at, slot.Name),
                        Ui.Reference("slot-" + at, new(UIVocabulary.SelectType, appKey + "/slot/" + at)),
                        Ui.Reference("slot-new-" + at, new(UIVocabulary.ButtonType, appKey + "/slot-new/" + at)),
                    ]);
                }),
                .. connectors.Select((connector, i) => Ui.Text("connector-" + i.ToString(CultureInfo.InvariantCulture), connector)),
            ]));
        }
        if ((shown.Secrets ?? []).Length > 0)
        {
            lists.Add(Ui.Text("requires-secrets-heading", AppsCopy.Secrets));
            lists.Add(Ui.List("requires-secrets",
            [
                .. shown.Secrets!.Select((secret, i) => Ui.Text("secret-" + i.ToString(CultureInfo.InvariantCulture), secret)),
                Ui.Reference("secrets-settings", new(UIVocabulary.ButtonType, appKey + "/to-variables")),
            ]));
        }
        return Ui.Card("requires", heading, null, [.. lists]);
    }

    private static UiNode[] Actions(string appKey, AppDetail d, bool installed)
    {
        List<UiNode> actions = [];
        if (Choosing(d)) { actions.Add(Ui.Reference("install", new(UIVocabulary.ButtonType, appKey + "/install"))); }
        if (installed && d.Status == "update") { actions.Add(Ui.Reference("update", new(UIVocabulary.ButtonType, appKey + "/update"))); }
        if (installed) { actions.Add(Ui.Reference("open", new(UIVocabulary.ButtonType, appKey + "/open"))); }
        if (MayRemove(d)) { actions.Add(Ui.Button("remove-ask", AppsCopy.Remove, "remove-ask", style: UiStyle.Danger)); }
        return [.. actions];
    }

    private static UiNode[] Toast(AppsModel m)
        => m.Toast is { Length: > 0 } toast ? [Ui.Toast("toast", toast, toast == AppsCopy.LoadFailed ? UiTone.Error : UiTone.Neutral)] : [];

    // The activations Apps.cs arms. The install request carries the consent digest of exactly what the panel shows
    // and the slots' chosen connections; a slot left on "Add a new ... connection" binds nothing.
    public static string InstallActivation(string brainId, AppDetail d, IReadOnlyList<string?> choices, Guid operationId)
    {
        Dictionary<string, string> bindings = new(StringComparer.Ordinal);
        var slots = Slots(d);
        for (var i = 0; i < slots.Length && i < choices.Count; i++)
        {
            if (choices[i] is { Length: > 0 } chosen && chosen != NewChoice) { bindings[slots[i].Name] = chosen; }
        }
        return JsonSerializer.Serialize(new
        {
            kind = "install",
            brainId,
            title = d.Title,
            request = new
            {
                @ref = new { publisher = d.Publisher, app = d.App },
                consent = AppConsent.Of(d.Requires),
                operationId,
                bindings = bindings.Count == 0 ? null : bindings,
            },
        }, Json);
    }

    public static string UpdateActivation(string brainId, AppDetail d)
        => JsonSerializer.Serialize(new { kind = "update", brainId, name = d.InstalledName, title = d.Title, consent = AppConsent.Of(d.Requires) }, Json);

    // Set only on the confirm Modal's button: the shell removes nothing without confirmed.
    public static string RemoveActivation(string brainId, AppDetail d)
        => JsonSerializer.Serialize(new { kind = "remove", brainId, name = d.InstalledName, title = d.Title, confirmed = true }, Json);

    public static string OpenActivation(string brainId, string name)
        => JsonSerializer.Serialize(new { kind = "workspace", brainId, application = name, operation = "open" }, Json);

    public static string VariablesActivation(string brainId)
        => JsonSerializer.Serialize(new { kind = "workspace", brainId, application = "settings", operation = "open", input = "variables" }, Json);

    public static string ConnectActivation(string brainId, string connector, string? connection = null)
        => JsonSerializer.Serialize(new { kind = "connections", brainId, connector, connection }, Json);

    // The connection a slot's Add new asks for, named for this app and slot.
    public static string SlotConnection(AppDetail d, AppSlot slot) => d.App + "-" + slot.Name;
}
