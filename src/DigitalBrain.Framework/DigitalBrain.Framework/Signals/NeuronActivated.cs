namespace DigitalBrain.Framework.Signals;

[GenerateSerializer, Alias("brain.neuron-activated")]
public sealed record NeuronActivated : NeuronActivity;
