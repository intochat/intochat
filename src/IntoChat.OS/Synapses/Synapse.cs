namespace IntoChat.OS;

/// <summary>
/// A durable connection: the watching neuron receives every <see cref="Signal"/> of one kind that
/// <see cref="Publisher"/> emits, across every process and restart.
/// </summary>
/// <remarks>
/// A synapse is a record, not a grain. It lives in the watching neuron's state, which is what makes
/// it durable; delivery bookkeeping such as buffers and watermarks is that neuron's own. Execution
/// is not a synapse: a program that runs is a neuron of its own kind, and these are its connections.
/// </remarks>
/// <param name="Publisher">The watched neuron.</param>
/// <param name="Signal">The kind of signal carried.</param>
[GenerateSerializer, Alias("synapse")]
public sealed record Synapse([property: Id(0)] NeuronId Publisher, [property: Id(1)] SignalId Signal);
