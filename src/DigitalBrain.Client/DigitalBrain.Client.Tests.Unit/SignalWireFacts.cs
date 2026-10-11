using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Client.Tests.Unit;

// A signal reaches a script as JSON: the brain runtime serializes the stamped signal as its own type,
// the session reads it back as the type the synapse subscribed to, both with RuntimeProtocol.Json.
public sealed class SignalWireFacts
{
    public sealed record Filtered(string Text) : Signal;

    [Fact]
    public void AStampedSignalKeepsItsPublisherAndTraceOriginAcrossTheWire()
    {
        Signal stamped = new Filtered("north")
        {
            Publisher = "acc-brain/apps/visualizer/views/sales/filter/region",
            Trace = new Trace { TraceId = "4bf92f3577b34da6a3ce929d0e0e4736", PayerAccountId = "acc", BrainId = "acc-brain", Origin = "acc-brain/edge" },
        };

        var json = JsonSerializer.Serialize(stamped, stamped.GetType(), RuntimeProtocol.Json);
        var read = JsonSerializer.Deserialize<Filtered>(json, RuntimeProtocol.Json)!;

        Assert.Equal("acc-brain/apps/visualizer/views/sales/filter/region", JsonNode.Parse(json)!["publisher"]!["name"]!.GetValue<string>());
        Assert.Equal("acc-brain/apps/visualizer/views/sales/filter/region", read.Publisher.Name);
        Assert.Equal("acc-brain/edge", read.Trace!.Origin.Name);
        Assert.Equal(stamped, read);
    }

    // A signal a script builds is not stamped until it is published.
    [Fact]
    public void AnUnstampedSignalCrossesTheWireWithoutAPublisher()
    {
        var unstamped = new Filtered("north");

        var read = JsonSerializer.Deserialize<Filtered>(JsonSerializer.Serialize(unstamped, RuntimeProtocol.Json), RuntimeProtocol.Json)!;

        Assert.Equal(unstamped, read);
    }

    [Fact]
    public void APublisherNameTheIdWouldRefuseIsRefusedOnTheWire()
    {
        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<Filtered>("""{"text":"north","publisher":{"name":"a\u0007b"}}""", RuntimeProtocol.Json));
    }
}
