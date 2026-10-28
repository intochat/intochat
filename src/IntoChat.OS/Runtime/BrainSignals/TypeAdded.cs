namespace IntoChat.OS;

/// <summary>A feature loaded into this brain brought a type the brain did not know. Published by the brain's <see cref="IBrain"/>.</summary>
/// <param name="Name">The type's full name, as <see cref="IBrainObserver.Types"/> lists it.</param>
[DigitalBrain.Signal("kernel.type-added")]
public sealed record TypeAdded(string Name) : DigitalBrain.Signal;
