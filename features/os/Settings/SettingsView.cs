using System.Globalization;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Preferences;
using IntoChat.OS.Flutter.Views;

public sealed record ModelRow(string Id, string Label, bool Available);

// An archived chat as Settings lists it: no id, so actions name the row index.
public sealed record ArchivedRow(string Title, DateTimeOffset UpdatedAt);

public sealed record ConnectionRow(string Connector, string Title, string Status, bool Connected);

public sealed record SettingsModel(string Tab, string AppKey, ModelRow Auto, IReadOnlyList<ModelRow> Models,
    IReadOnlyList<ArchivedRow> Archived, int ArchivedTotal, IReadOnlyList<ConnectionRow> Connections, bool ConfirmDelete, string? Toast);

// Pure composition of the Settings surface. Controls that read or write a person's data are neurons referenced by
// name; the tree itself carries labels and row indexes only, never an entry name, a value or a chat id.
public static class SettingsView
{
    public const double TabsHeight = 40;
    public const double OverlayExtent = 1;
    public const int ArchivedPage = 100;
    // Rows a tab shows at most, so the whole tree stays inside IView's 512 nodes.
    public const int ConnectionsShown = 64;

    public static UiNode Render(SettingsModel m, DateTimeOffset? now = null)
    {
        var tab = SettingsTabs.Id(m.Tab);
        UiNode[] children = [Tabs(tab), Body(tab, m, now ?? DateTimeOffset.UtcNow)];
        double[] extents = [TabsHeight, 0];
        if (m.Toast is { Length: > 0 } toast)
        {
            children = [.. children, Ui.Toast("toast", toast, toast == SettingsCopy.SaveFailed ? UiTone.Error : UiTone.Neutral)];
            extents = [.. extents, OverlayExtent];
        }
        return Ui.Column("settings", children, extents: extents);
    }

    // The default-model preference's choices: Auto first, unavailable models disabled with the fixed copy.
    public static PreferenceChoice[] ModelChoices(SettingsModel m) =>
        [.. new[] { m.Auto with { Label = SettingsCopy.AutoRow } }.Concat(m.Models)
            .Select(row => new PreferenceChoice(row.Id, row.Label, row.Available, row.Available ? null : SettingsCopy.ModelUnavailable))];

    private static UiNode Tabs(string active) => Ui.Tabs("tabs",
    [
        .. SettingsTabs.All.Select(tab => Ui.Tab("tab-" + tab.Id, tab.Label, "tab:" + tab.Id, tab.Id == active)),
        Ui.Button("tab-billing", SettingsCopy.Billing, "tab:billing", enabled: false, style: UiStyle.Ghost),
    ]);

    private static UiNode Body(string tab, SettingsModel m, DateTimeOffset now) => Ui.Column("tab-body-" + tab,
    [
        Ui.Text("heading", SettingsTabs.Label(tab)),
        Ui.Text("description", SettingsTabs.Description(tab)),
        .. tab switch
        {
            "general" => General(m.AppKey),
            "models" => Models(m),
            "keys" => Keys(m.AppKey),
            "connections" => Connections(m),
            "variables" => [Ui.Reference("variables-field", new(UIVocabulary.EntryControlType, m.AppKey + "/variables"))],
            "notifications" => [.. SettingsCopy.Notifications.Select(n => Preference("notify-" + n.Key, m.AppKey))],
            "archived" => Archived(m, now),
            _ => Danger(m),
        },
    ]);

    private static UiNode Preference(string name, string appKey)
        => Ui.Reference("pref-" + name, new(UIVocabulary.PreferenceType, appKey + "/pref/" + name));

    private static UiNode[] General(string appKey)
        => [.. new[] { "display-name", "theme", "compact", "reduced-motion", "new-tab" }.Select(name => Preference(name, appKey))];

    private static UiNode[] Models(SettingsModel m)
    {
        var rows = new[] { m.Auto with { Label = SettingsCopy.AutoRow } }.Concat(m.Models)
            .Select((row, i) => Ui.Card("model-" + i, row.Label, row.Available ? null : SettingsCopy.ModelUnavailable,
                row.Available ? [] : [Ui.Badge("model-" + i + "-unavailable", SettingsCopy.Unavailable, UiTone.Neutral)]));
        return [Ui.List("models", [.. rows]), Preference("default-model", m.AppKey)];
    }

    private static UiNode[] Keys(string appKey) =>
    [
        Ui.List("keys", [.. SettingsCopy.KeyProviders.Select(provider => Ui.Card("key-" + provider.Id, provider.Label, null,
            [Ui.Reference("key-field-" + provider.Id, new(UIVocabulary.EntryControlType, appKey + "/keys/" + provider.Id))]))]),
    ];

    private static UiNode[] Connections(SettingsModel m) =>
    [
        Ui.List("connections", [.. m.Connections.Take(ConnectionsShown).Select((row, i) => Ui.Card("connection-" + i, row.Title, row.Status,
            row.Connected ? [] : [Ui.Reference("connection-" + i + "-connect", new(UIVocabulary.ButtonType, m.AppKey + "/connect/" + row.Connector))]))]),
    ];

    private static UiNode[] Archived(SettingsModel m, DateTimeOffset now)
    {
        if (m.Archived.Count == 0) { return [Ui.Text("archived-empty", SettingsCopy.ArchivedEmpty)]; }
        UiNode[] nodes = [Ui.List("archived", [.. m.Archived.Take(ArchivedPage).Select((row, i) => Ui.Row("archived-" + i,
        [
            Ui.Text("archived-" + i + "-title", row.Title),
            Ui.Text("archived-" + i + "-when", Ago(row.UpdatedAt, now)),
            Ui.Button("unarchive-" + i, SettingsCopy.Unarchive, "unarchive:" + i),
        ]))])];
        return m.ArchivedTotal > ArchivedPage ? [.. nodes, Ui.Button("archived-more", SettingsCopy.ShowMore, "archived-more")] : nodes;
    }

    private static UiNode[] Danger(SettingsModel m)
    {
        UiNode[] nodes = [Ui.Card("delete-account", SettingsCopy.DeleteAccount, SettingsCopy.DeleteDetail,
            [Ui.Button("delete", SettingsCopy.DeleteAccount, "delete", style: UiStyle.Danger)])];
        return m.ConfirmDelete
            ? [.. nodes, Ui.Modal("confirm-delete", SettingsCopy.DeleteConfirmTitle, SettingsCopy.DeleteConfirmDetail,
                [
                    Ui.Reference("delete-go", new(UIVocabulary.ButtonType, m.AppKey + "/delete-account")),
                    Ui.Button("delete-cancel", SettingsCopy.Cancel, "delete-cancel", style: UiStyle.Ghost),
                ])]
            : nodes;
    }

    // As R2's archived tab showed it.
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age.TotalMinutes < 1) { return "Just now"; }
        if (age.TotalHours < 1) { return (int)age.TotalMinutes + " min ago"; }
        if (age.TotalDays < 1) { return (int)age.TotalHours + " h ago"; }
        if (age.TotalDays < 30) { return (int)age.TotalDays + " d ago"; }
        return at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
