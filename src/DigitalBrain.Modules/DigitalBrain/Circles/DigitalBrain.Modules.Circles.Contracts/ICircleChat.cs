using Orleans.Metadata;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.line")]
public sealed record CircleLine(
    [property: Id(0)] string Speaker,
    [property: Id(1)] string Text,
    [property: Id(2)] long Sequence);

// A circle's conversation, `{circle}/chat` in the circle brain: one line per Say, the speaker is the member account
// that said it. Until the circle app split, the older per-brain copies `{brain}/circles/{name}/chat` answer as before:
// the owner's copy numbers the lines and a member's copy pulls them.
[Alias("circles.chat"), DefaultGrainType("circles.chat")]
public interface ICircleChat : INeuron
{
    // A requestId makes the call idempotent: saying twice under one id is one line.
    Task Say(string text, string? requestId = null);
    Task<CircleLine[]> Read(long since);

    // Member copy -> owner copy; the speaker is the kernel-stamped caller's brain.
    [Internal] Task<long> Relay(string text, string? requestId = null);
    [Internal] Task<CircleLine[]> Pull(long after);
    [Internal, Orleans.Concurrency.OneWay] Task Notify();

    // A circle brain id, or (older copies) a circle copy's key.
    static string Key(string circleBrainId) => $"{circleBrainId}/chat";
}
