using Orleans.Metadata;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.line")]
public sealed record CircleLine(
    [property: Id(0)] string Speaker,
    [property: Id(1)] string Text,
    [property: Id(2)] long Sequence);

// A circle's conversation as one brain holds it. The owner's copy numbers the lines and hands them to
// every member's copy, so each brain reads and watches the same chat inside itself.
[Alias("circles.chat"), DefaultGrainType("circles.chat")]
public interface ICircleChat : INeuron
{
    Task Say(string text);
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<CircleLine[]> Read(long since);

    [Internal] Task<long> Relay(string speaker, string text, string fromBrain);
    [Internal] Task Hear(CircleLine line);

    static string Key(string circleKey) => $"{circleKey}/chat";
}
