using Orleans.Metadata;

namespace DigitalBrain.Circles;

// Role: owner, member or invited.
[GenerateSerializer, Alias("circles.entry")]
public sealed record CircleEntry(
    [property: Id(0)] string Name,
    [property: Id(1)] string Owner,
    [property: Id(2)] string Role);

// The brain's own circles: one neuron per brain, keyed `{brain}/circles`. Its copies of circles and chats
// report every change here, so this neuron is the one publisher an app watches (CircleTouched).
[Alias("circles.index"), DefaultGrainType("circles.index")]
public interface ICircles : INeuron
{
    Task<CircleEntry[]> Names();

    [Internal] Task Touch(string name, string owner, string kind, string role);

    static string Key(string brainId) => $"{brainId}/circles";
}
