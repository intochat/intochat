using IntoChat.OS.Modules.Apps;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IntoChat.OS;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Card;
using IntoChat.OS.Modules.Flutter.Layout;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Text.Signals;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Time.Reminders;

// A view is a durable definition the synapses share: what to query, which columns are filters and
// how often to run. Every layout is derived from the registry, so composing is idempotent.
public sealed record View(string Id, string Title, string Kind, string Source, string Sql, string[] Filters, int RefreshSeconds, string? FilterGroup = null);

public static class Views
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<View[]> All(IntoChatOSConnection brain, string appKey)
    {
        var text = (await brain.Get<IText>(appKey + "/views").Read()).Markdown;
        return text.Length == 0 ? [] : JsonSerializer.Deserialize<View[]>(text, Json) ?? [];
    }

    public static async Task<View?> Current(IntoChatOSConnection brain, string appKey, string id)
        => (await All(brain, appKey)).FirstOrDefault(view => view.Id == id);

    public static Task Save(IntoChatOSConnection brain, string appKey, View[] views)
        => brain.Get<IText>(appKey + "/views").Set(JsonSerializer.Serialize(views, Json));

    // The set of neurons the views name, following the registry text: a synapse watches it with
    // React<T> or On<T> and maps a signal's Publisher back to the views it belongs to. Views sharing a
    // neuron name it once.
    public static Sources Registry<T>(IntoChatOSConnection brain, string appKey, Func<View, IEnumerable<string>> keys) where T : class
        => Sources.Named<IText, TextChanged, T>(brain.Get<IText>(appKey + "/views"), async (registry, _) =>
        {
            var text = (await registry.Read()).Markdown;
            var views = text.Length == 0 ? [] : JsonSerializer.Deserialize<View[]>(text, Json) ?? [];
            return views.SelectMany(keys).Distinct(StringComparer.Ordinal).Select(key => brain.Get<T>(key)).ToArray();
        });

    public static async Task Compose(IntoChatOSConnection brain, string appKey, View view)
    {
        var root = $"{appKey}/views/{view.Id}";
        var children = new List<UiChildRef>();
        foreach (var column in view.Filters)
        {
            // Configuring announces a change to every view sharing the field, so a configured field is left alone.
            var field = brain.Get<ITextField>($"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}");
            var label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(column.Replace('_', ' '));
            var configured = await field.Read();
            if (configured.Label != label || configured.Kind != "text") { await field.Configure(label, "text"); }
            children.Add(new("textfield", $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}"));
        }
        children.Add(new("card", $"{root}/status"));
        children.Add(new(view.Kind, $"{root}/{view.Kind}"));
        var layout = brain.Get<ILayout>($"{root}/layout");
        var current = await layout.Read();
        var definition = new LayoutDefinition("list", children.ToArray());
        if (current.Definition.Mode != definition.Mode || !Same(current.Definition.Children, definition.Children)) { await layout.Set(definition, current.Revision); }

    }

    // A registered view with an interval owns a durable reminder; run-on-tick turns its ticks into refreshes.
    public static async Task Schedule(IntoChatOSConnection brain, string appKey, View view)
    {
        if (view.RefreshSeconds > 0) { await brain.Get<IReminder>($"{appKey}/views/{view.Id}/refresh").Start(TimeSpan.FromSeconds(view.RefreshSeconds), TimeSpan.FromSeconds(view.RefreshSeconds)); }
    }

    public static async Task ComposeHome(IntoChatOSConnection brain, string appKey, View[] views)
    {
        var list = brain.Get<ICard>(appKey + "/list");
        await list.Set("Views", views.Length == 0 ? "No views yet. Ask the assistant to show a table or a chart." : string.Join("\n", views.Select(view => $"- {view.Title} ({view.Kind}, {view.Id})")));
        var main = brain.Get<ILayout>(appKey + "/main");
        var current = await main.Read();
        foreach (var view in views) { await Compose(brain, appKey, view); }
        UiChildRef[] children = [new("card", appKey + "/list"), new("card", appKey + "/status"),
            .. views.Select(view => new UiChildRef("layout", $"{appKey}/views/{view.Id}/layout"))];
        if (current.Definition.Mode != "list" || !Same(current.Definition.Children, children))
        { await main.Set(new("list", children), current.Revision); }
        await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", appKey + "/main"), Views: [new("full", new("layout", appKey + "/main"))]));
    }

    private static bool Same(UiChildRef[] left, UiChildRef[] right) => left.Length == right.Length && left.Zip(right).All(pair => pair.First == pair.Second);
}
