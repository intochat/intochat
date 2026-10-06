namespace DigitalBrain.Signals;

[GenerateSerializer, Alias("brain.neuron-activated")]
public sealed record NeuronActivated : NeuronActivity;
