namespace DigitalBrain;

/// <summary>
/// A typed fact a neuron publishes. Signals are the only thing that flows between neurons.
/// </summary>
/// <remarks>
/// Derive a sealed record per fact (<c>TimerTick</c>, <c>Clicked</c>) and give it an
/// <see cref="AliasAttribute"/>; that alias is its <see cref="SignalId"/>.
/// </remarks>
[GenerateSerializer, Alias("brain.signal")]
public record Signal
{
    /// <summary>
    /// The neuron this signal came from, the same id a <see cref="Synapse"/> connects to.
    /// </summary>
    /// <remarks>
    /// Stamped by the runtime at publish, overwriting whatever the caller set, so provenance cannot
    /// be forged. It is routing metadata, not proof of identity: authorization is decided against
    /// the caller context, never against this value.
    /// </remarks>
    [Id(0)] public NeuronId Publisher { get; init; }

    /// <summary>
    /// The trace this signal belongs to: why it was published and who pays for what it causes.
    /// </summary>
    /// <remarks>Stamped by the runtime at publish exactly like <see cref="Publisher"/>.</remarks>
    [Id(1)] public Trace? Trace { get; init; }
}
