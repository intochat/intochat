namespace DigitalBrain;

// Published when an established brain wakes: once right after Establish, and again on every
// later activation. Handlers are idempotent; this is the signal the operating system itself
// boots on, and any app can subscribe to it the same way.
[GenerateSerializer, Alias("brain.activated")]
public sealed record Activated(
    [property: Id(0)] string BrainId,
    [property: Id(1)] string OwnerAccountId,
    [property: Id(2)] string Name) : Signal;
