namespace DigitalBrain;

// Neuron is the watched neuron's grain id ("time.timer/tea"); Signal is the signal type's name ("TimerTick").
[GenerateSerializer, Alias("synapse.watch")]
public sealed record SynapseWatch([property: Id(0)] string Neuron, [property: Id(1)] string Signal, [property: Id(2)] int Pending);
