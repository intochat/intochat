namespace IntoChat.OS;

// One retained signal: its place in the publisher's retention order and the stamped fact itself.
[GenerateSerializer, Alias("brain.retained-signal")]
public sealed record RetainedSignal([property: Id(0)] long Sequence, [property: Id(1)] Signal Signal);
