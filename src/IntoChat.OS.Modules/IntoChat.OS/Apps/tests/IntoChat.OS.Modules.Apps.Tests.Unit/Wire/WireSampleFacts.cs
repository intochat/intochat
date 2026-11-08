using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Authority.Signals;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Wire;

// The samples under src/Assets/wire are the contract the Dart mirrors round-trip too. They go through plain Web options with no enum
// converter, so the enums' own [JsonConverter] attributes decide: the OS enums here and the framework's (FlowView.WaitingFor,
// relations[].kind, since DigitalBrain 0.17.0-flows.4) all serialize as PascalCase strings. journaled-signal.json is the /signals page
// entry (SignalView); its payload is the signal serialized with System.Text.Json defaults, so its members are PascalCase.
public sealed class WireSampleFacts
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("presence.json", typeof(Presence))]
    [InlineData("focus.json", typeof(Focus))]
    [InlineData("flow-view.json", typeof(FlowView))]
    [InlineData("approval.json", typeof(Approval))]
    [InlineData("journaled-signal.json", typeof(SignalView))]
    public void EverySampleRoundTrips(string file, Type type)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wire", file));
        var value = JsonSerializer.Deserialize(text, type, Options);
        Assert.NotNull(value);

        var again = JsonSerializer.SerializeToNode(value, type, Options);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), again), $"{file} did not round-trip:\n{again}");
    }
}
