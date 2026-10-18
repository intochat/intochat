namespace IntoChat.OS;

/// <summary>
/// Published on every wake of a created brain, including right after <see cref="Created"/>.
/// </summary>
/// <remarks>
/// This is the signal the operating system boots on and any program may subscribe to. Because it
/// fires on every activation, handlers are idempotent: they catch up, they do not initialize.
/// </remarks>
/// <param name="Owner">The account the brain belongs to.</param>
[GenerateSerializer, Alias("brain.activated")]
public sealed record Activated([property: Id(0)] Owner Owner) : Signal;
