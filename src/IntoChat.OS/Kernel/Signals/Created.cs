namespace IntoChat.OS;

/// <summary>
/// The first signal in a brain, published exactly once by <see cref="IBrain.Create"/>.
/// </summary>
/// <remarks>
/// Handlers provision what a new brain is born with. <see cref="IBrain.Create"/> awaits them, so a
/// brain is complete before its creator sees it. One-time work belongs here; catch-up work belongs
/// on <see cref="Activated"/>.
/// </remarks>
/// <param name="Owner">The account the brain was created for.</param>
[GenerateSerializer, Alias("brain.created")]
public sealed record Created([property: Id(0)] Owner Owner) : Signal;
