using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.UiKit;

/// <summary>A page of the UI Kit: a scenario or one catalog kind.</summary>
/// <param name="Id">The page id: the scenario id, or the kind (which is also the slot key of a control neuron's page).</param>
/// <param name="Title">The nav title.</param>
/// <param name="Group">Scenario, Control neuron or Data node.</param>
public sealed record UiKitPage(string Id, string Title, string Group);

/// <summary>The UI Kit's pages: the scenarios first, then every catalog kind (control neurons and data nodes) in kind order.</summary>
public static class UiKitPages
{
    public const string Scenario = "Scenario";
    public const string ControlNeuron = "Control neuron";
    public const string DataNode = "Data node";

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
    public const string SampleSourceKey = "uikit-samples";

    public const string First = Message;

    public static IReadOnlyList<ComboBoxOption> Models { get; } =
        [new("sonnet", "Claude Sonnet"), new("haiku", "Claude Haiku"), new("local", "Local model")];

    public static IReadOnlyList<UiKitPage> Scenarios { get; } =
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

    /// <summary>The control neurons of this assembly by catalog kind (class name without "Neuron", camel-cased).</summary>
    public static IReadOnlyDictionary<string, Type> NeuronKinds { get; } = typeof(ControlNeuron<,>).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && IsControl(type))
        .ToDictionary(type => JsonNamingPolicy.CamelCase.ConvertName(type.Name.EndsWith("Neuron", StringComparison.Ordinal) ? type.Name[..^"Neuron".Length] : type.Name), StringComparer.Ordinal);

    /// <summary>The data node kinds, from their [ViewNode] attributes.</summary>
    public static IReadOnlyDictionary<string, Type> NodeKinds { get; } = ViewNodeAttribute.DiscoverAll();

    /// <summary>Every page, in nav order.</summary>
    public static IReadOnlyList<UiKitPage> All { get; } =
    [
        .. Scenarios,
        .. NeuronKinds.Keys.Select(kind => new UiKitPage(kind, kind, ControlNeuron))
            .Concat(NodeKinds.Keys.Select(kind => new UiKitPage(kind, kind, DataNode)))
            .OrderBy(page => page.Id, StringComparer.Ordinal),
    ];

    public static UiKitPage? Find(string? id) => All.FirstOrDefault(page => page.Id == id);

    private static bool IsControl(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ControlNeuron<,>)) { return true; }
        }
        return false;
    }

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
            if (json.Deserialize<Node>(JsonSerializerOptions.Web) is { } node) { nodes.Add(node); }
        }
        return nodes;
    }

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
        => new(string.Create(CultureInfo.InvariantCulture, $"uikit-{number}"), string.Create(CultureInfo.InvariantCulture, $"#{number}"), (number * 37 % 90) + 10);
}
