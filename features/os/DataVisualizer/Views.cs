using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DigitalBrain;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Time.Reminders;

// A view is a durable definition the synapses share: what to query, which columns are filters and
// how often to run. Every window is derived from the registry, so composing is idempotent.
public sealed record View(string Id, string Title, string Kind, string Source, string Sql, string[] Filters, int RefreshSeconds, string? FilterGroup = null);

public static class Views
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<View[]> All(DigitalBrainConnection brain, string appKey)
    {
        var text = (await brain.Get<IText>(appKey + "/views").Read()).Markdown;
        return text.Length == 0 ? [] : JsonSerializer.Deserialize<View[]>(text, Json) ?? [];
    }

    public static async Task<View?> Current(DigitalBrainConnection brain, string appKey, string id)
        => (await All(brain, appKey)).FirstOrDefault(view => view.Id == id);

    public static Task Save(DigitalBrainConnection brain, string appKey, View[] views)
        => brain.Get<IText>(appKey + "/views").Set(JsonSerializer.Serialize(views, Json));

    // The set of neurons the views name, following the registry text: a synapse watches it with
    // React<T> or On<T> and maps a signal's Publisher back to the views it belongs to. Views sharing a
    // neuron name it once.
    public static Sources Registry<T>(DigitalBrainConnection brain, string appKey, Func<View, IEnumerable<string>> keys) where T : INeuron
        => Sources.Named<IText, TextChanged>(brain.Get<IText>(appKey + "/views"), async (registry, _) =>
        {
            var text = (await registry.Read()).Markdown;
            var views = text.Length == 0 ? [] : JsonSerializer.Deserialize<View[]>(text, Json) ?? [];
            return views.SelectMany(keys).Distinct(StringComparer.Ordinal).Select(key => (INeuron)brain.Get<T>(key)).ToArray();
        });

    public static async Task Compose(DigitalBrainConnection brain, string appKey, View view)
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
        var surface = brain.Get<ISurface>($"{root}/surface");
        var state = await surface.Read();
        if (state.Definition.Title != view.Title || state.Definition.Children.Length != 1) { await surface.Set(new(view.Title, [new("layout", $"{root}/layout")]), state.Revision); }
    }

    // A registered view with an interval owns a durable reminder; run-on-tick turns its ticks into refreshes.
    public static async Task Schedule(DigitalBrainConnection brain, string appKey, View view)
    {
        if (view.RefreshSeconds > 0) { await brain.Get<IReminder>($"{appKey}/views/{view.Id}/refresh").Start(TimeSpan.FromSeconds(view.RefreshSeconds), TimeSpan.FromSeconds(view.RefreshSeconds)); }
    }

    public static async Task ComposeHome(DigitalBrainConnection brain, string appKey, View[] views)
    {
        var list = brain.Get<ICard>(appKey + "/list");
        await list.Set("Views", views.Length == 0 ? "No views yet. Ask the assistant to show a table or a chart." : string.Join("\n", views.Select(view => $"- {view.Title} ({view.Kind}, {view.Id})")));
        var main = brain.Get<ILayout>(appKey + "/main");
        var current = await main.Read();
        if (current.Definition.Children.Length == 0) { await main.Set(new("column", [new("card", appKey + "/list")]), current.Revision); }
        var surface = brain.Get<ISurface>(appKey + "/surface");
        var state = await surface.Read();
        if (state.Definition.Title != "Data Visualizer") { await surface.Set(new("Data Visualizer", [new("layout", appKey + "/main")]), state.Revision); }
    }

    public static Task OpenWindowAsync(DigitalBrainConnection brain, string appKey, CancellationToken ct)
        => EnsureWindowAsync(brain, appKey + "/window", "Data Visualizer", appKey + "/surface", Guid.NewGuid().ToString(), ct);

    // Each view has its own window over its surface; opening is idempotent, so a replayed show opens it once.
    public static Task OpenViewWindowAsync(DigitalBrainConnection brain, string appKey, View view, string operation, CancellationToken ct)
        => EnsureWindowAsync(brain, $"{appKey}/views/{view.Id}/window", view.Title, $"{appKey}/views/{view.Id}/surface", operation, ct);

    public static Task CloseViewWindowAsync(DigitalBrainConnection brain, string appKey, string id, string operation, CancellationToken ct)
    {
        var window = $"{appKey}/views/{id}/window";
        return ChangeWorkspaceAsync(brain, state => async workspace =>
            { await workspace.Close(window, state.Revision, operation); }, ct);
    }

    private static Task EnsureWindowAsync(DigitalBrainConnection brain, string window, string title, string surface, string operation, CancellationToken ct)
    {
        var reference = WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, surface));
        // The operation id makes a replayed show or close idempotent.
        return ChangeWorkspaceAsync(brain, state => async workspace =>
            { await workspace.Open(new(operation, window, title, reference, state.Revision)); }, ct);
    }

    private static async Task ChangeWorkspaceAsync(DigitalBrainConnection brain, Func<WorkspaceState, Func<IWorkspace, Task>?> plan, CancellationToken ct)
    {
        var workspace = brain.Get<IWorkspace>(brain.Setting("Brain")!);
        ct.ThrowIfCancellationRequested();
        var state = await workspace.Read();
        if (plan(state) is { } change) { await change(workspace); }
    }

    private static bool Same(UiChildRef[] left, UiChildRef[] right) => left.Length == right.Length && left.Zip(right).All(pair => pair.First == pair.Second);
}
