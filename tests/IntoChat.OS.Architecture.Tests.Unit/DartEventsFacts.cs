using DigitalBrain.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

// One typed Dart helper per control event ([EventArgs]): emit<Control><Event>(ctx, node, <args>) posts the payload under `event`.
public sealed class DartEventsFacts
{
    private static readonly string Target = Path.Combine(Repository.Root, "ui", "lib", "s" + "rc", "generated", "node_events.dart");

    [Fact]
    public void TheDartEventHelpersAreGenerated()
        => NodeSchema.CheckOrRegenerate(Target, NodeSchema.GenerateEvents(), nameof(TheDartEventHelpersAreGenerated));

    [Fact]
    public void EveryEventOfAGeneratedKindHasAHelper()
    {
        var generated = NodeSchema.GenerateEvents();
        foreach (var (type, _) in NodeSchema.DerivedKinds())
        {
            foreach (var (property, _) in NodeSchema.Events(type))
            {
                Assert.Contains($"node.{NodeSchema.JsonName(property)}, {{", generated, StringComparison.Ordinal);
            }
        }
        Assert.Equal("event", EventPayload.Key);
    }
}
