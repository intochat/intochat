using Orleans.Metadata;

namespace IntoChat.OS.Circles;

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
    [Shared] Task PublishPost(string postKey, string requestId);

    static string Key(string circleBrainId) => $"{circleBrainId}/chat";
}

// Module-only typed readers: not INeuron contracts, absent from scripts and edge resolution.
[Alias("circles.state-reader"), DefaultGrainType("circles.circle")]
public interface ICircleStateReader : IGrainWithStringKey
{
    Task<CircleState> State();
    // Immutable snapshot only; consumers must intersect it with current trust membership.
    [Orleans.Concurrency.AlwaysInterleave, Orleans.Concurrency.ReadOnly]
    Task<string[]> Members();
}

[Alias("circles.post-reader"), DefaultGrainType("circles.chat")]
public interface ICirclePostReader : IGrainWithStringKey
{
    Task<CircleLine[]> PostReferences();
}
