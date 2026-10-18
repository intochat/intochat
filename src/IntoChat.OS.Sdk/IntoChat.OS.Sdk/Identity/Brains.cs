namespace IntoChat.OS.Framework.Platform.Identity;

public enum BrainKind { Personal, Circle }

[GenerateSerializer, Alias("identity.brain-entry")]
public sealed record BrainEntry([property: Id(0)] string Id, [property: Id(1)] BrainKind Kind);

[GenerateSerializer, Alias("identity.brain-provision")]
public sealed record BrainProvision([property: Id(0)] Member Member, [property: Id(1)] bool NewBrain);
