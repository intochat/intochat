using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Wire;

// The samples under src/Assets/wire are the contract the Dart mirrors round-trip too.
public sealed class WireSampleFacts
{
    // The framework's RuntimeProtocol.Json shape (Web defaults) with enums as strings, which is what the samples show.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new SignalJson() },
    };

    [Theory]
    [InlineData("presence.json", typeof(Presence))]
    [InlineData("focus.json", typeof(Focus))]
    [InlineData("flow-view.json", typeof(FlowView))]
    [InlineData("approval.json", typeof(Approval))]
    [InlineData("journaled-signal.json", typeof(DigitalBrain.JournaledSignal))]
    public void EverySampleRoundTrips(string file, Type type)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wire", file));
        var value = JsonSerializer.Deserialize(text, type, Options);
        Assert.NotNull(value);

        var again = JsonSerializer.SerializeToNode(value, type, Options);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), again), $"{file} did not round-trip:\n{again}");
    }

    // A journaled signal's Signal is abstract; the sample carries it as {"type": alias, ...the signal's own members}.
    private sealed class SignalJson : JsonConverter<DigitalBrain.Signal>
    {
        private static readonly Dictionary<string, Type> Known = typeof(FlowDone).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DigitalBrain.Signal)) && t.GetCustomAttributes(typeof(DigitalBrain.SignalAttribute), false).Length == 1)
            .ToDictionary(t => ((DigitalBrain.SignalAttribute)t.GetCustomAttributes(typeof(DigitalBrain.SignalAttribute), false)[0]).Alias);

        public override DigitalBrain.Signal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader)!.AsObject();
            var alias = node["type"]!.GetValue<string>();
            node.Remove("type");
            return (DigitalBrain.Signal)node.Deserialize(Known[alias], options)!;
        }

        public override void Write(Utf8JsonWriter writer, DigitalBrain.Signal value, JsonSerializerOptions options)
        {
            var type = value.GetType();
            var alias = ((DigitalBrain.SignalAttribute)type.GetCustomAttributes(typeof(DigitalBrain.SignalAttribute), false)[0]).Alias;
            var node = JsonSerializer.SerializeToNode(value, type, options)!.AsObject();
            var result = new JsonObject { ["type"] = alias };
            foreach (var (k, v) in node.ToArray()) { node.Remove(k); result[k] = v; }
            result.WriteTo(writer);
        }
    }
}
