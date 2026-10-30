using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DigitalBrain.UI;
using IntoChat.OS;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Views.Signals;
using IntoChat.OS.Modules.Time.Reminders;
using UI = DigitalBrain.UI.Ui;
using ViewUi = IntoChat.OS.Modules.Flutter.Views.Ui;

// A view is a durable definition the synapses share: what to query, which columns are filters and
// how often to run. Every declarative surface is derived from the registry, so composing is idempotent.
public sealed record DataViewDefinition(
    string Id,
    string Title,
    string Kind,
    string Source,
    string Sql,
    string[] Filters,
    int RefreshSeconds,
    string? FilterGroup = null);

public sealed record DataVisualizerState(
    string Status = "No views yet. Ask the assistant to show a table or a chart.",
    DataViewDefinition[]? Views = null,
    string[]? Columns = null,
    string[][]? Rows = null)
{
    public DataVisualizerState() : this(
        "No views yet. Ask the assistant to show a table or a chart.",
        [],
        ["Column"],
        [])
    {
    }
}

[DigitalBrain.App("datavisualizer", Icon = "bar_chart", Category = "Analytics")]
public sealed class DataVisualizerScriptApp : DigitalBrain.App<DataVisualizerState>
{
    [DigitalBrain.On("open")]
    public Task Open()
    {
        Set(State);
        return Task.CompletedTask;
    }

    [DigitalBrain.On("show")]
    public Task Show(string? input)
    {
        var title = string.IsNullOrWhiteSpace(input) ? "Query View" : input.Trim();
        var next = new DataViewDefinition("v-1", title, "table", "sql", "SELECT 1", [], 0);
        var views = (State.Views ?? []).Append(next).ToArray();
        Set(State with { Status = $"Loaded {views.Length} view(s)", Views = views });
        return Task.CompletedTask;
    }

    public DigitalBrain.UI.View Render() => new(
        "Data Visualizer",
        new Column(
        [
            new Surface(
                [
                    new Text(State.Status),
                ],
                Kind: SurfaceKind.Elevated,
                Title: "Data Visualizer",
                Subtitle: $"{(State.Views ?? []).Length} active view(s)"),
            new DataTable(
                Columns: State.Columns ?? ["Column"],
                Rows: State.Rows ?? []),
        ]));
}

public static class Views
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IView RegistryView(IntoChatOSConnection brain, string appKey)
        => brain.Get<IView>(appKey + "/views");

    public static async Task<DataViewDefinition[]> All(IntoChatOSConnection brain, string appKey)
    {
        var current = await RegistryView(brain, appKey).Read();
        var text = current?.Root.Text ?? "";
        return text.Length == 0 ? [] : JsonSerializer.Deserialize<DataViewDefinition[]>(text, Json) ?? [];
    }

    public static async Task<DataViewDefinition?> Current(IntoChatOSConnection brain, string appKey, string id)
        => (await All(brain, appKey)).FirstOrDefault(view => view.Id == id);

    public static async Task Save(IntoChatOSConnection brain, string appKey, DataViewDefinition[] views)
    {
        var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
        var publisher = new ViewPublisher(RegistryView(brain, appKey), revision);
        await publisher.Publish(ViewUi.Text("views", JsonSerializer.Serialize(views, Json)));
    }

    public static Sources Registry<T>(IntoChatOSConnection brain, string appKey, Func<DataViewDefinition, IEnumerable<string>> keys) where T : class
        => Sources.Named<IView, ViewChanged, T>(RegistryView(brain, appKey), async (registry, _) =>
        {
            var current = await registry.Read();
            var text = current?.Root.Text ?? "";
            var views = text.Length == 0 ? [] : JsonSerializer.Deserialize<DataViewDefinition[]>(text, Json) ?? [];
            return views.SelectMany(keys).Distinct(StringComparer.Ordinal).Select(key => brain.Get<T>(key)).ToArray();
        });

    public static async Task Compose(IntoChatOSConnection brain, string appKey, DataViewDefinition view)
    {
        var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
        var root = $"{appKey}/views/{view.Id}";
        var publisher = new ViewPublisher(brain.Get<IView>($"{root}/view"), revision);
        await publisher.Publish(ViewUi.Column(
            view.Id,
            [
                ViewUi.Text("title", view.Title),
                ViewUi.Text("kind", view.Kind),
                ViewUi.Text("status", "Ready"),
            ]));
    }

    public static async Task Schedule(IntoChatOSConnection brain, string appKey, DataViewDefinition view)
    {
        if (view.RefreshSeconds > 0)
        {
            await brain.Get<IReminder>($"{appKey}/views/{view.Id}/refresh")
                .Start(TimeSpan.FromSeconds(view.RefreshSeconds), TimeSpan.FromSeconds(view.RefreshSeconds));
        }
    }

    public static async Task ComposeHome(IntoChatOSConnection brain, string appKey, DataViewDefinition[] views)
    {
        var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
        foreach (var view in views) { await Compose(brain, appKey, view); }
        var summary = views.Length == 0
            ? "No views yet. Ask the assistant to show a table or a chart."
            : string.Join("\n", views.Select(view => $"- {view.Title} ({view.Kind}, {view.Id})"));
        var publisher = new ViewPublisher(brain.Get<IView>(appKey + "/view"), revision);
        await publisher.Publish(ViewUi.Column(
            "datavisualizer",
            [
                ViewUi.Text("list", summary),
                .. views.Select(view => ViewUi.Text(view.Id, $"{view.Title} ({view.Kind})")),
            ]));
        await brain.Get<IApp>(appKey).Present(
            revision,
            new(
                new Sphere("icon.svg", []),
                Main: new(UIVocabulary.ViewType, appKey + "/view"),
                Views: [new("full", new(UIVocabulary.ViewType, appKey + "/view"))]));
    }

    public static async Task SetStatus(IntoChatOSConnection brain, string appKey, string title, string message)
    {
        var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
        var publisher = new ViewPublisher(brain.Get<IView>(appKey + "/status"), revision);
        await publisher.Publish(ViewUi.Text("status", $"{title}: {message}"));
    }

    public static async Task SetFilterValue(IntoChatOSConnection brain, string key, string value)
    {
        var revision = long.Parse(brain.Setting("ProgramRevision") ?? "1", CultureInfo.InvariantCulture);
        var publisher = new ViewPublisher(brain.Get<IView>(key), revision);
        await publisher.Publish(ViewUi.Text("filter", value));
    }

    public static async Task<string> ReadFilterValue(IntoChatOSConnection brain, string key)
    {
        var current = await brain.Get<IView>(key).Read();
        return current?.Root.Text ?? "";
    }
}
