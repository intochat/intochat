using Orleans.Metadata;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.line")]
public sealed record CircleLine(
    [property: Id(0)] string Speaker,
    [property: Id(1)] string Text,
    [property: Id(2)] long Sequence);

// A circle's conversation as one brain holds it. The owner's copy numbers the lines (the speaker is
// always the brain that said it); a member's copy pulls them in order, so every brain reads and
// watches the same chat inside itself.
[Alias("circles.chat"), DefaultGrainType("circles.chat")]
public interface ICircleChat : INeuron
{
    Task Say(string text);
    Task<CircleLine[]> Read(long since);

    // Member copy -> owner copy; the speaker is the kernel-stamped caller's brain.
    [Internal] Task<long> Relay(string text);
    [Internal] Task<CircleLine[]> Pull(long after);
    [Internal, Orleans.Concurrency.OneWay] Task Notify();

    static string Key(string circleKey) => $"{circleKey}/chat";
}
