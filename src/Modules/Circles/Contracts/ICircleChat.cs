using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Circles;

public sealed record CircleLine(
    string Speaker,
    string Text,
    long Sequence);

// A circle's conversation, `{circle}/chat` in the circle brain: one line per Say, the speaker is the member account
// that said it.
// A plain neuron (06-08).
[DigitalBrain.Contracts.NeuronAlias("circles.chat"), DigitalBrain.Contracts.DefaultNeuronType("circles.chat")]
public interface ICircleChat
{
    // A requestId makes the call idempotent: saying twice under one id is one line.
    Task Say(string text, string? requestId = null);
    Task<CircleLine[]> Read(long since);
    [Shared] Task PublishPost(string postKey, string requestId);

    static string Key(string circleBrainId) => $"{circleBrainId}/chat";
}

// Module-only typed readers ([Internal] as a whole, 06-08): absent from scripts and edge resolution.
[DigitalBrain.Contracts.NeuronAlias("circles.state-reader"), DigitalBrain.Contracts.DefaultNeuronType("circles.circle")]
[Internal]
public interface ICircleStateReader
{
    Task<CircleState> State();
    // Immutable snapshot only; consumers must intersect it with current trust membership.
    [DigitalBrain.Contracts.Interleaves, DigitalBrain.Contracts.ReadOnlyCall]
    Task<string[]> Members();
}

[DigitalBrain.Contracts.NeuronAlias("circles.post-reader"), DigitalBrain.Contracts.DefaultNeuronType("circles.chat")]
[Internal]
public interface ICirclePostReader
{
    Task<CircleLine[]> PostReferences();
}
