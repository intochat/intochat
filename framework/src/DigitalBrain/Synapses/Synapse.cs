namespace DigitalBrain;

/// <summary>A durable route: signals of type <paramref name="Signal"/> published by <paramref name="From"/> reach <paramref name="To"/>.</summary>
public sealed record Synapse(NeuronId From, Type Signal, NeuronId To);
