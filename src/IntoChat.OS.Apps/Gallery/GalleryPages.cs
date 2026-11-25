using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;
using IntoChat.OS.Modules.UI.Controls;

namespace IntoChat.OS.Apps.Gallery;

/// <summary>A page of the Gallery: a scenario or one catalog kind.</summary>
/// <param name="Id">The page id: the scenario id, or the kind (which is also the slot key of a control neuron's page).</param>
/// <param name="Title">The nav title.</param>
/// <param name="Group">Scenario, Control neuron or Data node.</param>
public sealed record GalleryPage(string Id, string Title, string Group);

/// <summary>The Gallery's pages: the scenarios first, then every catalog kind (control neurons and data nodes) in kind order.</summary>
public static class GalleryPages
{
    public const string Scenario = "Scenarios";
    public const string ControlNeuron = "Controls";
    public const string DataNode = "Data nodes";

    public const string Message = "message";
    public const string FailedReply = "failed-reply";
    public const string CardInChat = "card-in-chat";
    public const string ActivityRow = "activity-row";
    public const string Composer = "composer";
    public const string Empty = "empty";
    public const string Waiting = "waiting";
    public const string LiveChart = "live-chart";
    public const string TableQuery = "table-query";

    /// <summary>The slot key of the composer page's model picker.</summary>
    public const string ModelPicker = "model-picker";

    /// <summary>The slot key of the controlled switch on the toggle switch page.</summary>
    public const string ControlledSwitch = "toggleSwitch-controlled";

    /// <summary>The slot key of the page nav.</summary>
    public const string Nav = "nav";

    /// <summary>The key of the sample table source the table pages read.</summary>
    public const string SampleSourceKey = "gallery-samples";

    /// <summary>The child key of the query table page's table with inline rows.</summary>
    public const string InlineTable = "queryTable-inline";

    /// <summary>The key of the form page's secret field.</summary>
    public const string SecretField = "apiKey";

    /// <summary>The columns of the inline table.</summary>
    public static IReadOnlyList<QueryTableColumn> InlineColumns { get; } = [new("task", "Task"), new("owner", "Owner"), new("stage", "Stage")];

    /// <summary>The inline table's rows, keyed by task id: only these can be selected.</summary>
    public static IReadOnlyList<QueryTableRow> InlineRows { get; } =
    [
        new("notes", ["Write release notes", "Ada", "To do"]),
        new("ship", ["Ship 2.0", "Ben", "Doing"]),
        new("plan", ["Plan", "Cy", "Done"]),
        new("review", ["Review the board", "Ada", "Doing"]),
    ];

    public const string First = Message;

    public static IReadOnlyList<ComboBoxOption> Models { get; } =
        [new("sonnet", "Claude Sonnet"), new("haiku", "Claude Haiku"), new("local", "Local model")];

    public static IReadOnlyList<GalleryPage> Scenarios { get; } =
    [
        new(Message, "Message", Scenario),
        new(FailedReply, "Failed reply", Scenario),
        new(CardInChat, "Card in chat", Scenario),
        new(ActivityRow, "Activity row", Scenario),
        new(Composer, "Composer with model picker", Scenario),
        new(Empty, "Empty", Scenario),
        new(Waiting, "Waiting", Scenario),
        new(LiveChart, "Live chart", Scenario),
        new(TableQuery, "Table query", Scenario),
    ];

    /// <summary>The control contracts by catalog kind: the neuron type without "control." (control.checkBox -> checkBox).</summary>
    public static IReadOnlyDictionary<string, Type> NeuronKinds { get; } = typeof(ICheckBoxControl).Assembly.GetExportedTypes()
        .Where(type => type.IsInterface && IsControl(type))
        .ToDictionary(type => type.GetCustomAttribute<DefaultNeuronTypeAttribute>()!.Type["control.".Length..], StringComparer.Ordinal);

    /// <summary>The data node kinds, from their [ViewNode] attributes.</summary>
    public static IReadOnlyDictionary<string, Type> NodeKinds { get; } = ViewNodeAttribute.DiscoverAll();

    /// <summary>Every page, in nav order.</summary>
    public static IReadOnlyList<GalleryPage> All { get; } =
    [
        .. Scenarios,
        .. NeuronKinds.Keys.Select(kind => new GalleryPage(kind, kind, ControlNeuron))
            .Concat(NodeKinds.Keys.Select(kind => new GalleryPage(kind, kind, DataNode)))
            .OrderBy(page => page.Id, StringComparer.Ordinal),
    ];

    public static GalleryPage? Find(string? id) => All.FirstOrDefault(page => page.Id == id);

    // The contracts assembly holds only control contracts (UiVerticalSliceFacts); each names its control.* neuron type.
    private static bool IsControl(Type type)
        => type.GetCustomAttribute<DefaultNeuronTypeAttribute>()?.Type.StartsWith("control.", StringComparison.Ordinal) == true;

    /// <summary>The [Showcase] examples of a data node kind, each read back as the node: a whole node, or one property value shown as {kind, prop: value}.</summary>
    public static IReadOnlyList<Node> Examples(string kind, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var named = type.GetConstructors().SelectMany(ctor => ctor.GetParameters())
            .SelectMany(parameter => parameter.GetCustomAttributes<ShowcaseAttribute>().Select(showcase => (parameter.Name!, showcase)))
            .Concat(type.GetProperties().SelectMany(property => property.GetCustomAttributes<ShowcaseAttribute>().Select(showcase => (property.Name, showcase))));
        List<Node> nodes = [];
        foreach (var (name, showcase) in named)
        {
            if (!seen.Add(showcase.ExampleJson)) { continue; }
            var value = JsonDocument.Parse(showcase.ExampleJson).RootElement.Clone();
            var json = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("kind", out _)
                ? value
                : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["kind"] = JsonSerializer.SerializeToElement(kind),
                    [JsonNamingPolicy.CamelCase.ConvertName(name)] = value,
                });
            if (Captured(kind, WithDefaults(type, JsonNode.Parse(json.GetRawText()))).Deserialize<Node>(JsonSerializerOptions.Web) is { } node) { nodes.Add(node); }
        }
        return nodes;
    }

    // Every command in an example (an object with a string target and signal) becomes a ShowcaseCommand to the Gallery itself carrying the
    // original signal and args: the example's made-up signals are logged instead of refused. The [Showcase] examples stay as written.
    // The serializer reads a missing constructor parameter as default(T), not its declared default: an example that leaves out
    // Enabled or Visible would come out disabled and hidden. Fill in every declared default the example does not write.
    private static JsonNode? WithDefaults(Type type, JsonNode? json)
    {
        if (json is not JsonObject node) { return json; }
        foreach (var parameter in type.GetConstructors().SelectMany(ctor => ctor.GetParameters()))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!);
            if (parameter.HasDefaultValue && parameter.DefaultValue is bool or string or int or double && !node.ContainsKey(name))
            {
                node[name] = JsonValue.Create(parameter.DefaultValue);
            }
        }
        return node;
    }

    private static JsonNode? Captured(string kind, JsonNode? json)
    {
        switch (json)
        {
            case JsonObject command when command["target"] is JsonValue target && target.TryGetValue<string>(out _)
                && command["signal"] is JsonValue signal && signal.TryGetValue<string>(out var alias):
                var captured = new GalleryShowcaseCommand(kind, alias, command["args"]?.ToJsonString() ?? "null");
                return new JsonObject
                {
                    ["target"] = ".",
                    ["signal"] = ShowcaseCommandSignal,
                    ["args"] = JsonSerializer.SerializeToNode(captured, JsonSerializerOptions.Web),
                };
            case JsonObject node:
                foreach (var name in node.Select(property => property.Key).ToArray()) { node[name] = Captured(kind, node[name]?.DeepClone()); }
                return node;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) { array[i] = Captured(kind, array[i]?.DeepClone()); }
                return array;
            default:
                return json;
        }
    }

    /// <summary>The alias of <see cref="GalleryShowcaseCommand"/>.</summary>
    public const string ShowcaseCommandSignal = "gallery.showcaseCommand";

    /// <summary>The kanban page's columns.</summary>
    public static IReadOnlyList<KanbanColumnItem> Columns { get; } = [new("todo", "To do"), new("doing", "Doing", WipLimit: 2), new("done", "Done", "check")];

    /// <summary>The kanban page's cards before any move.</summary>
    public static IReadOnlyList<KanbanCardItem> Cards { get; } =
        [new("notes", "todo", "Write release notes", Tag: "docs"), new("ship", "doing", "Ship 2.0", "Due Friday", Assignee: "Ada"), new("plan", "done", "Plan")];

    /// <summary>The cards with <paramref name="cardId"/> moved into <paramref name="columnId"/> at <paramref name="index"/> among that column's
    /// other cards (past the end appends); an unknown card leaves them as they are.</summary>
    public static KanbanCardItem[] Move(IReadOnlyList<KanbanCardItem> cards, string cardId, string columnId, int index)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.FirstOrDefault(card => card.Id == cardId) is not { } moved) { return [.. cards]; }
        List<KanbanCardItem> rest = [.. cards.Where(card => card.Id != cardId)];
        int[] inColumn = [.. rest.Select((card, at) => (card, at)).Where(entry => entry.card.ColumnId == columnId).Select(entry => entry.at)];
        var at = index < inColumn.Length ? inColumn[Math.Max(index, 0)] : inColumn.Length > 0 ? inColumn[^1] + 1 : rest.Count;
        rest.Insert(at, moved with { ColumnId = columnId });
        return [.. rest];
    }

    /// <summary>The points the live chart pages start with (fixed event ids, so writing them again is ignored while the chart holds them).</summary>
    public static IReadOnlyList<LivePoint> ChartSeed { get; } = [new("seed-1", "Mon", 40), new("seed-2", "Tue", 65), new("seed-3", "Wed", 52)];

    /// <summary>The messages a scenario page writes into its chat thread when shown (idempotent by id), or none.</summary>
    public static IReadOnlyList<ThreadMessage> Seed(string page) => page switch
    {
        Message =>
        [
            new("m1", "user", "What's on my calendar today?"),
            new("m2", "assistant", "Two meetings: design review at 10:00 and a 1:1 at 15:30.") { Model = "Claude Sonnet" },
        ],
        FailedReply =>
        [
            new("f1", "user", "Summarise my week"),
            new("f2", "assistant", "", "failed") { Failure = "The model did not answer." },
        ],
        CardInChat =>
        [
            new("c1", "user", "How did the week go?"),
            new("c2", "assistant", "Here is the weekly report.")
            {
                Cards = [new Card("Weekly report", "Five tasks closed, two open.", [new Progress("Done", 0.7)])],
            },
        ],
        ActivityRow =>
        [
            new("a1", "user", "Find the Q3 report"),
            new("a2", "assistant", "Found it in Documents.")
            {
                Activities = [new ActivityNode("s1", "Searched files", "done", "3 matches"), new ActivityNode("s2", "Read report-q3.pdf", "done")],
            },
        ],
        Composer => [new("p1", "assistant", "Pick a model, then write a message.")],
        Waiting =>
        [
            new("w1", "user", "Draft a reply to Ana"),
            new("w2", "assistant", "", "running"),
        ],
        _ => [],
    };

    /// <summary>The next sample point the Append button adds.</summary>
    public static LivePoint Point(int number)
        => new(string.Create(CultureInfo.InvariantCulture, $"gallery-{number}"), string.Create(CultureInfo.InvariantCulture, $"#{number}"), (number * 37 % 90) + 10);
}
