namespace DigitalBrain;

/// <summary>
/// Tells an observer that a watched neuron has published: who, and the newest journal sequence.
/// Carries no signal; the observer reads what it missed with <see cref="INeuron.Read"/>.
/// </summary>
/// <remarks>
/// Best effort and coalescable: a lost or merged nudge loses nothing, because the next one (or a
/// read on reconnect) covers every sequence up to its own.
/// </remarks>
[GenerateSerializer, Alias("brain.nudge")]
public sealed record SignalNudge([property: Id(0)] NeuronId Publisher, [property: Id(1)] long Sequence);
