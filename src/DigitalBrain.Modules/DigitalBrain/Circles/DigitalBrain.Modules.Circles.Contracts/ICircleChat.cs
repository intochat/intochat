using Orleans.Metadata;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.line")]
public sealed record CircleLine(
    [property: Id(0)] string Speaker,
    [property: Id(1)] string Text,
    [property: Id(2)] long Sequence);

// A circle's conversation, `{circle}/chat` in the circle brain: one line per Say, the speaker is the member account
// that said it.
[Alias("circles.chat"), DefaultGrainType("circles.chat")]
public interface ICircleChat : INeuron
{
    // A requestId makes the call idempotent: saying twice under one id is one line.
    Task Say(string text, string? requestId = null);
    Task<CircleLine[]> Read(long since);

    static string Key(string circleBrainId) => $"{circleBrainId}/chat";
}
